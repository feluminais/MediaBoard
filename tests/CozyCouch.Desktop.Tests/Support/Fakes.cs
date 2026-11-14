using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CozyCouch.Core.Activity;
using CozyCouch.Core.Library;
using CozyCouch.Core.Metadata;
using CozyCouch.Core.Playback;
using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop.Tests.Support;

/// <summary>Records what the UI asked it to play, and on which thread.</summary>
internal sealed class FakePlayback : IPlaybackService
{
    private readonly List<(int FileId, bool FromStart, int ThreadId)> _played = [];

    public IReadOnlyList<(int FileId, bool FromStart, int ThreadId)> Played
    {
        get
        {
            lock (_played) return _played.ToList();
        }
    }

    public event Action<int, string>? AskFinished;

    /// <summary>The ids passed to <see cref="AnswerFinished"/>, with the answer.</summary>
    public List<(int FileId, bool Finished)> Answers { get; } = [];

    /// <summary>When set, PlayAsync waits for it: a test can keep a start "in progress".</summary>
    public Task? PlayGate { get; set; }

    /// <summary>Raised on the calling thread; the real service raises it on a player thread.</summary>
    public void RaiseAskFinished(int fileId, string title) => AskFinished?.Invoke(fileId, title);

    public Task PlayAsync(int fileId, bool fromStart, CancellationToken ct)
    {
        lock (_played) _played.Add((fileId, fromStart, Environment.CurrentManagedThreadId));
        return PlayGate ?? Task.CompletedTask;
    }

    public void AnswerFinished(int fileId, bool finished)
    {
        lock (Answers) Answers.Add((fileId, finished));
    }

    public event Action? ProgressChanged;

    public void RaiseProgressChanged() => ProgressChanged?.Invoke();

    /// <summary>The player a test says is running; null when nothing plays.</summary>
    public string? CurrentPlayer { get; set; }

    /// <summary>Every SavePositionAsync and StopAsync call, in order: "save", "stop:close" or "stop:leave".</summary>
    public List<string> Calls { get; } = [];

    public Task SavePositionAsync(CancellationToken ct)
    {
        lock (Calls) Calls.Add("save");
        return Task.CompletedTask;
    }

    public Task StopAsync(bool closePlayer, CancellationToken ct)
    {
        lock (Calls) Calls.Add(closePlayer ? "stop:close" : "stop:leave");
        CurrentPlayer = null;
        return Task.CompletedTask;
    }
}

internal static class Posters
{
    /// <summary>A loader for a machine with no posters on disk and no network.</summary>
    public static PosterLoader None() => new((_, _, _) => Task.FromResult<string?>(null));

    /// <summary>A loader that answers <c>local:{path}</c> for every poster and records what it was asked for.</summary>
    public static PosterLoader Local(List<string> asked) => new((path, _, _) =>
    {
        lock (asked) asked.Add(path);
        return Task.FromResult<string?>($"local:{path}");
    });

    /// <summary>Writes a plain PNG of the given size and returns its path.</summary>
    public static string WritePng(string directory, string name, int width, int height)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
        {
            var pixels = new byte[frame.RowBytes * height];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 80; pixels[i + 2] = 40; pixels[i + 3] = 255; }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
        }

        bitmap.Save(path, new PngBitmapEncoderOptions());
        return path;
    }
}

/// <summary>An <see cref="ISearchService"/> that answers from lists the test sets.</summary>
internal sealed class FakeSearch : ISearchService
{
    public List<SearchCandidate> Candidates { get; } = [];

    public OnlineState State { get; set; } = OnlineState.Ok;

    public TitleDetails? Details { get; set; }

    public List<string> Queries { get; } = [];

    /// <summary>TMDB ids the library already holds, with the local title id SearchAsync reports for them.</summary>
    public Dictionary<int, int> LibraryIds { get; } = [];

    /// <summary>Answers SearchAsync instead of <see cref="Candidates"/>; a test can hold a search open or look at its token.</summary>
    public Func<string, CancellationToken, Task<SearchResults>>? OnSearch { get; set; }

    public Task<SearchResults> SearchAsync(string query, CancellationToken ct)
    {
        lock (Queries) Queries.Add(query);
        if (OnSearch is not null) return OnSearch(query, ct);

        var online = State == OnlineState.Ok
            ? Candidates.Select(c => new OnlineResult(c, LibraryIds.TryGetValue(c.TmdbId, out var id) ? id : null)).ToList()
            : [];
        return Task.FromResult(new SearchResults([], online, State));
    }

    /// <summary>Every GetOnlineDetailsAsync call, in order.</summary>
    public List<(int TmdbId, TitleKind Kind)> DetailRequests { get; } = [];

    /// <summary>Answers GetOnlineDetailsAsync (on the calling thread) instead of <see cref="Details"/>; a test can block or flip the hub offline.</summary>
    public Func<int, TitleKind, TitleDetails?>? OnDetails { get; set; }

    public Task<TitleDetails?> GetOnlineDetailsAsync(int tmdbId, TitleKind kind, CancellationToken ct)
    {
        lock (DetailRequests) DetailRequests.Add((tmdbId, kind));
        var answer = OnDetails is null ? Details : OnDetails(tmdbId, kind);
        ct.ThrowIfCancellationRequested(); // like the real client, a request cancelled while it waits ends with the cancellation
        return Task.FromResult(answer);
    }

    /// <summary>The online seasons this fake knows, by (TMDB id, season); GetOnlineSeasonAsync answers null for the rest.</summary>
    public Dictionary<(int TmdbId, int Season), SeasonDetails> Seasons { get; } = [];

    /// <summary>Every GetOnlineSeasonAsync call, in order.</summary>
    public List<(int TmdbId, int Season)> SeasonRequests { get; } = [];

    /// <summary>Answers GetOnlineSeasonAsync instead of <see cref="Seasons"/>; a test can hold a season open.</summary>
    public Func<int, int, CancellationToken, Task<SeasonDetails?>>? OnSeason { get; set; }

    public Task<SeasonDetails?> GetOnlineSeasonAsync(int tmdbId, int season, CancellationToken ct)
    {
        lock (SeasonRequests) SeasonRequests.Add((tmdbId, season));
        if (OnSeason is not null) return OnSeason(tmdbId, season, ct);

        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Seasons.TryGetValue((tmdbId, season), out var hit) ? hit : null);
    }

    /// <summary>The palette commands this fake offers; <see cref="Commands"/> filters them like the real service.</summary>
    public List<PaletteCommand> CommandList { get; } = [];

    public IReadOnlyList<PaletteCommand> Commands(string filter) => CommandList
        .Where(c => filter.Length == 0 || c.Label.Split(' ').Any(w => w.StartsWith(filter.Trim(), StringComparison.OrdinalIgnoreCase)))
        .ToList();
}

/// <summary>Builds the title page's collaborators for a test; every seam is a field the test can read or replace.</summary>
internal sealed class Titles
{
    public Titles(FakeLibrary library, ActivityHub hub, FakePlayback? playback = null)
    {
        Library = library;
        Hub = hub;
        Playback = playback ?? new FakePlayback();
        Search = new FakeSearch();
    }

    public FakeLibrary Library { get; }

    public ActivityHub Hub { get; }

    public FakePlayback Playback { get; }

    public FakeSearch Search { get; }

    public PosterLoader Posters { get; set; } = Support.Posters.None();

    public string? Player { get; set; } = "MPC-HC";

    public Func<DeletePreview, Task<bool>> ConfirmDelete { get; set; } = _ => Task.FromResult(true);

    public List<string> NotMediaAsked { get; } = [];

    public bool ConfirmNotMedia { get; set; } = true;

    public Func<string, Task<bool?>> AskFinished { get; set; } = _ => Task.FromResult<bool?>(null);

    public int Identified { get; private set; }

    public int SettingsOpened { get; private set; }

    public int WentBack { get; private set; }

    public List<int> Replaced { get; } = [];

    public PlaybackLauncher Launcher => _launcher ??= new PlaybackLauncher(Playback, Hub, title => AskFinished(title));

    private PlaybackLauncher? _launcher;

    public TitleServices Services() => new(
        Library, Search, Hub, Posters, Launcher, () => Player, preview => ConfirmDelete(preview),
        name => { NotMediaAsked.Add(name); return Task.FromResult(ConfirmNotMedia); },
        () => { Identified++; return Task.CompletedTask; });

    public TitleNavigation Navigation() => new(() => WentBack++, () => SettingsOpened++, Replaced.Add);

    public TitleViewModel Page(int titleId) => new(titleId, Services(), Navigation());

    public TitleViewModel OnlinePage(int tmdbId, TitleKind kind) => TitleViewModel.Online(tmdbId, kind, Services(), Navigation());
}
