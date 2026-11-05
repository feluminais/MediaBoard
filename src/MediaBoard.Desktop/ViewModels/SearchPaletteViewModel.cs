using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Core.Remote;

namespace MediaBoard.Desktop.ViewModels;

/// <summary>What a palette choice can ask the shell to do. All but <see cref="Rescan"/> run on the UI thread and return at once.</summary>
/// <param name="OpenTitle">Opens a library title's page.</param>
/// <param name="OpenOnlineTitle">Opens the page of a TMDB title that is not in the library.</param>
/// <param name="Rescan">Rescans every enabled folder. The task never throws.</param>
/// <param name="ShowHidden">Shows the grid with "Show hidden" on.</param>
public sealed record PaletteActions(
    Action<int> OpenTitle,
    Action<int, TitleKind> OpenOnlineTitle,
    Action OpenSettings,
    Func<Task> Rescan,
    Action ShowHidden);

public enum PaletteRowKind { Header, Note, Item }

/// <summary>
/// One line of the palette: a group header ("Library"), a note that cannot be chosen ("Offline"), or a choice.
/// <see cref="Key"/> names what the row stands for, so the selection can stay on it when the list is rebuilt.
/// </summary>
public sealed partial class PaletteRow(string key, string text, string? detail, PaletteRowKind kind, Action? open) : ObservableObject
{
    public string Key { get; } = key;

    public string Text { get; } = text;

    /// <summary>The year, type and rating of a title; null for everything else.</summary>
    public string? Detail { get; } = detail;

    public bool HasDetail => Detail is not null;

    public bool IsHeader => kind == PaletteRowKind.Header;

    public bool IsNote => kind == PaletteRowKind.Note;

    public bool IsSelectable => kind == PaletteRowKind.Item;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Open() => open?.Invoke();
}

/// <summary>
/// The Ctrl+F palette. One box: text searches the library (a result per keystroke, never the network) and, after a
/// 300 ms typing pause, TMDB; a leading <c>&gt;</c> lists commands instead. Every keystroke cancels the search in
/// flight and a late answer is dropped. The library part asks <see cref="ILibrary.SearchLocal"/> directly: the
/// search service's <c>SearchAsync</c> waits for TMDB, so it is only called once the pause has passed. A viewer's library
/// is a server away, so for a viewer nothing goes out until the pause. Then both go at once, each filling its own group,
/// so library hits still don't wait for TMDB.
/// </summary>
public sealed partial class SearchPaletteViewModel : ObservableObject
{
    public const string LibraryHeader = "Library";
    public const string OnlineHeader = "Online";
    public const string CommandsHeader = "Commands";
    public const string OfflineNote = "Offline";
    public const string NoKeyChoice = "Add a TMDB key";
    public const string ServerNoKeyNote = "This library has no TMDB key";
    public const string SearchingNote = "Searching…";
    public const string KeyRejectedNote = "TMDB rejected the key";
    public const string NoMatches = "No matches";
    public const string NoCommands = "No commands";

    /// <summary>How long typing must pause before TMDB is asked.</summary>
    public static readonly TimeSpan OnlineDelay = TimeSpan.FromMilliseconds(300);

    private readonly ISearchService _search;
    private readonly ILibrary _library;
    private readonly PaletteActions _actions;
    private readonly ActivityHub _hub;
    private readonly TimeProvider _time;
    private readonly Action _close;
    private readonly bool _searchesServer;
    private readonly BackgroundQuery _libraryQuery;

    private CancellationTokenSource? _onlineCancel;
    private IReadOnlyList<TitleCard> _libraryHits = [];
    private IReadOnlyList<OnlineResult> _onlineHits = [];
    private OnlineState? _onlineState; // null until TMDB answered for the current text
    private Task _libraryTask = Task.CompletedTask;
    private Task _onlineTask = Task.CompletedTask;
    private string? _lastFailure;
    private bool _libraryPending; // a viewer's library group hasn't answered for the current text

    public SearchPaletteViewModel(
        ISearchService search,
        ILibrary library,
        PaletteActions actions,
        ActivityHub hub,
        TimeProvider time,
        Action close,
        AppCapabilities? capabilities = null)
    {
        _search = search;
        _library = library;
        _actions = actions;
        _hub = hub;
        _time = time;
        _close = close;
        _searchesServer = capabilities?.Connection is not null;
        _libraryQuery = new BackgroundQuery(hub, "the search results", _searchesServer ? OnLibraryFailed : null, capabilities);
    }

    /// <summary>What the box holds.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>Headers, notes and choices, in the order shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRows))]
    public partial IReadOnlyList<PaletteRow> Rows { get; private set; } = [];

    /// <summary>Index into <see cref="Rows"/> of the row Enter opens; -1 when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selected))]
    public partial int SelectedIndex { get; private set; } = -1;

    /// <summary>"No matches" once everything has answered and nothing was found; otherwise null.</summary>
    [ObservableProperty]
    public partial string? EmptyText { get; private set; }

    public bool HasRows => Rows.Count > 0;

    public PaletteRow? Selected => SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null;

    /// <summary>Completes when the library part of the latest keystroke has been applied or dropped.</summary>
    public Task LibraryTask => _libraryTask;

    /// <summary>Completes when the latest keystroke has been answered in full (or dropped): library and online.</summary>
    public Task Settled => Task.WhenAll(_libraryTask, _onlineTask);

    /// <summary>Empties the box and the results, for the next time the palette opens.</summary>
    public void Reset()
    {
        Query = "";
        Update(""); // the text may already have been empty, which raises no change
    }

    /// <summary>Stops the search in flight; the palette closed.</summary>
    public void Cancel()
    {
        _onlineCancel?.Cancel();
        _libraryQuery.Discard();
    }

    [RelayCommand]
    private void MoveDown() => Move(1);

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void OpenSelected() => Selected?.OpenCommand.Execute(null);

    [RelayCommand]
    private void Close() => _close();

    /// <summary>Escape peels one layer: the typed text first, then the palette.</summary>
    [RelayCommand]
    private void Escape()
    {
        if (Query.Length > 0) Query = "";
        else _close();
    }

    partial void OnQueryChanged(string value) => Update(value);

    private void Update(string text)
    {
        Cancel();
        _onlineHits = [];
        _onlineState = null;

        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '>') // nothing typed, or commands: no search runs
        {
            _libraryHits = [];
            _libraryPending = false;
            _libraryTask = Task.CompletedTask;
            _onlineTask = Task.CompletedTask;
            Rebuild(keepSelection: false);
        }
        else
        {
            var cancel = _onlineCancel = new CancellationTokenSource();
            if (_searchesServer)
            {
                // The last text's hits must go with it: Enter would open one of them.
                _libraryHits = [];
                _libraryPending = true;
                _libraryTask = Task.CompletedTask;
            }
            else
            {
                _libraryTask = SearchLibraryAsync(trimmed);
            }

            _onlineTask = RunOnlineAsync(trimmed, cancel.Token);
            Rebuild(keepSelection: false); // the online group of the last keystroke goes at once
        }
    }

    private Task SearchLibraryAsync(string text) => _libraryQuery.RunAsync(
        () => _library.SearchLocal(text, SearchService.MaxLibraryResults),
        hits =>
        {
            _libraryHits = hits;
            _libraryPending = false;
            Rebuild(keepSelection: false);
        });

    private void OnLibraryFailed()
    {
        _libraryPending = false;
        Rebuild(keepSelection: true);
    }

    // Waits out the pause, then asks the search service on the thread pool: its library part is synchronous work.
    private async Task RunOnlineAsync(string text, CancellationToken ct)
    {
        var library = Task.CompletedTask;
        try
        {
            await Task.Delay(OnlineDelay, _time, ct);
            if (_searchesServer) library = _libraryTask = SearchLibraryAsync(text);

            var results = await Task.Run(() => _search.SearchAsync(text, ct), ct);
            if (ct.IsCancellationRequested) return;

            _lastFailure = null;
            _onlineHits = results.Online;
            _onlineState = results.State;
            Rebuild(keepSelection: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer keystroke, or the palette closed.
        }
        catch (OperationCanceledException)
        {
            // TMDB timed out without us cancelling: the same as being offline.
            ShowOffline();
        }
        catch (Exception e) when (_searchesServer && e is ServerUnreachableException or ApiException or IdentityChangedException)
        {
            // The status bar already says what's wrong with the connection.
            ShowOffline();
        }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested) Report(e);
        }

        await library;
    }

    private void ShowOffline()
    {
        _onlineHits = [];
        _onlineState = OnlineState.Offline;
        Rebuild(keepSelection: true);
    }

    private void Report(Exception e)
    {
        if (_searchesServer) ShowOffline(); // or "Searching" would stay
        if (e.Message == _lastFailure) return;

        _lastFailure = e.Message;
        _hub.Log(ActivityLevel.Error, $"Couldn't search online: {e.Message}");
    }

    private void Rebuild(bool keepSelection)
    {
        var kept = keepSelection ? Selected?.Key : null;
        var rows = new List<PaletteRow>();
        var trimmed = Query.Trim();
        string? empty = null;

        if (trimmed.Length > 0 && trimmed[0] == '>')
        {
            var commands = _search.Commands(trimmed[1..].Trim());
            if (commands.Count == 0) empty = NoCommands;
            else
            {
                rows.Add(Header(CommandsHeader));
                rows.AddRange(commands.Select(c => Item($"command:{c.Id}", c.Label, null, () => RunCommand(c.Id))));
            }
        }
        else if (trimmed.Length > 0)
        {
            AddLibraryGroup(rows);
            AddOnlineGroup(rows);
            var waiting = _libraryPending || (_searchesServer && _onlineState is null);
            if (rows.Count == 0 && waiting) rows.Add(new PaletteRow("note:searching", SearchingNote, null, PaletteRowKind.Note, null));
            else if (rows.Count == 0 && _onlineState is not null) empty = NoMatches;
        }

        Rows = rows;
        EmptyText = empty;
        var keptIndex = kept is null ? -1 : rows.FindIndex(r => r.IsSelectable && r.Key == kept);
        Select(keptIndex >= 0 ? keptIndex : rows.FindIndex(r => r.IsSelectable));
    }

    // The library's own hits, then TMDB hits the library already has (the local search missed them, for instance
    // because it matched the original title): they open the local page. Online lists them again, as the full TMDB page.
    private void AddLibraryGroup(List<PaletteRow> rows)
    {
        var shown = new HashSet<int>(_libraryHits.Select(h => h.TitleId));
        var group = _libraryHits
            .Select(card => LibraryItem(card.TitleId, card.Name, card.Year, card.Kind, card.Rating))
            .ToList();

        foreach (var hit in _onlineHits)
        {
            if (hit.LibraryTitleId is not { } id || !shown.Add(id)) continue;
            group.Add(LibraryItem(id, hit.Candidate.Name, hit.Candidate.Year, hit.Candidate.Kind, hit.Candidate.Rating));
        }

        if (group.Count == 0) return;
        rows.Add(Header(LibraryHeader));
        rows.AddRange(group);
    }

    private void AddOnlineGroup(List<PaletteRow> rows)
    {
        switch (_onlineState)
        {
            case OnlineState.Ok:
                // Every hit is listed, library titles too: the library page only has the seasons on disk,
                // the online page has all of them with their ratings.
                var hits = _onlineHits.Select(h => h.Candidate).ToList();
                if (hits.Count == 0) return;
                rows.Add(Header(OnlineHeader));
                rows.AddRange(hits.Select(c => Item(
                    $"online:{c.Kind}:{c.TmdbId}",
                    c.Name,
                    Describe(c.Year, c.Kind, c.Rating),
                    () => _actions.OpenOnlineTitle(c.TmdbId, c.Kind))));
                break;
            case OnlineState.Offline:
                rows.Add(Header(OnlineHeader));
                rows.Add(new PaletteRow("note:offline", OfflineNote, null, PaletteRowKind.Note, null));
                break;
            case OnlineState.NoKey:
                rows.Add(Header(OnlineHeader));
                rows.Add(_searchesServer
                    ? new PaletteRow("note:no-key", ServerNoKeyNote, null, PaletteRowKind.Note, null)
                    : Item("key:missing", NoKeyChoice, null, _actions.OpenSettings));
                break;
            case OnlineState.KeyRejected:
                rows.Add(Header(OnlineHeader));
                rows.Add(new PaletteRow("note:rejected", KeyRejectedNote, null, PaletteRowKind.Note, null));
                break;
        }
    }

    private PaletteRow LibraryItem(int titleId, string name, int? year, TitleKind kind, double? rating) =>
        Item($"title:{titleId}", name, Describe(year, kind, rating), () => _actions.OpenTitle(titleId));

    private static PaletteRow Header(string text) => new($"header:{text}", text, null, PaletteRowKind.Header, null);

    // Choosing closes the palette first, then does the thing.
    private PaletteRow Item(string key, string text, string? detail, Action action) =>
        new(key, text, detail, PaletteRowKind.Item, () =>
        {
            _close();
            action();
        });

    private void RunCommand(string id)
    {
        switch (id)
        {
            case "rescan":
                _ = _actions.Rescan();
                break;
            case "settings":
                _actions.OpenSettings();
                break;
            case "hidden":
                _actions.ShowHidden();
                break;
        }
    }

    /// <summary>"1999 · Movie · ★ 8.2"; a missing year or rating is left out.</summary>
    private static string Describe(int? year, TitleKind kind, double? rating)
    {
        var parts = new List<string>(3);
        if (year is { } y) parts.Add(y.ToString(CultureInfo.InvariantCulture));
        parts.Add(kind == TitleKind.Movie ? "Movie" : "Show");
        if (rating is { } r) parts.Add("★ " + r.ToString("0.0", CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

    // The selection wraps round, and headers and notes are skipped.
    private void Move(int step)
    {
        var choices = new List<int>();
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].IsSelectable) choices.Add(i);
        }

        if (choices.Count == 0) return;

        var at = choices.IndexOf(SelectedIndex);
        var next = at < 0 ? (step > 0 ? 0 : choices.Count - 1) : (at + step + choices.Count) % choices.Count;
        Select(choices[next]);
    }

    private void Select(int index)
    {
        foreach (var row in Rows) row.IsSelected = false;
        SelectedIndex = index;
        OnPropertyChanged(nameof(Selected)); // the rows may be new while the index is not
        if (Selected is { } selected) selected.IsSelected = true;
    }
}
