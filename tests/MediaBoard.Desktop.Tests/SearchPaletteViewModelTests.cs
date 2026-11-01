using Avalonia.Headless.XUnit;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Tests;

public class SearchPaletteViewModelTests
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    private static readonly SearchCandidate Matrix = new(603, TitleKind.Movie, "The Matrix", null, 1999, 50, 8.2, null, null);
    private static readonly SearchCandidate Severance = new(95396, TitleKind.Show, "Severance", null, 2022, 40, 8.4, null, null);

    private sealed class Rig
    {
        public Rig()
        {
            var actions = new PaletteActions(
                id => Calls.Add($"title:{id}"),
                (id, kind) => Calls.Add($"online:{id}:{kind}"),
                () => Calls.Add("settings"),
                () =>
                {
                    Calls.Add("rescan");
                    return Task.CompletedTask;
                },
                () => Calls.Add("hidden"));
            Palette = new SearchPaletteViewModel(Search, Library, actions, Hub, Clock, () => Closed++);
        }

        public FakeLibrary Library { get; } = new();

        public FakeSearch Search { get; } = new();

        public ManualClock Clock { get; } = new();

        public ActivityHub Hub { get; } = new();

        public List<string> Calls { get; } = [];

        public int Closed { get; private set; }

        public SearchPaletteViewModel Palette { get; }

        public IEnumerable<string> Texts => Palette.Rows.Select(r => r.Text);

        /// <summary>Types the text and waits for the library group, which needs no pause.</summary>
        public async Task TypeAsync(string text)
        {
            Palette.Query = text;
            await Ui.WaitUntil(() => Palette.LibraryTask.IsCompleted);
        }

        /// <summary>Lets the 300 ms pause pass and waits for the online group.</summary>
        public async Task PauseAsync()
        {
            Clock.Advance(Pause);
            await Ui.WaitUntil(() => Palette.Settled.IsCompleted);
        }
    }

    private static Rig WithLibrary(params TitleCard[] cards)
    {
        var rig = new Rig();
        rig.Library.LocalSearch = _ => cards;
        return rig;
    }

    [AvaloniaFact]
    public async Task Typing_ShowsLibraryImmediately_OnlineAfter300ms()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat", year: 1995));
        rig.Search.Candidates.Add(Matrix);

        rig.Palette.Query = "mat";
        await Ui.WaitUntil(() => rig.Palette.Rows.Count > 0);

        Assert.Equal(["Library", "Heat"], rig.Texts);
        Assert.Empty(rig.Search.Queries); // TMDB hears nothing during the pause

        rig.Clock.Advance(Pause - TimeSpan.FromMilliseconds(1));
        Assert.Empty(rig.Search.Queries);

        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await Ui.WaitUntil(() => rig.Palette.Rows.Any(r => r.Text == "The Matrix"));

        Assert.Equal(["mat"], rig.Search.Queries);
        Assert.Equal(["Library", "Heat", "Online", "The Matrix"], rig.Texts);
    }

    [AvaloniaFact]
    public async Task Typing_AgainBeforeThePause_RestartsIt()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"));

        rig.Palette.Query = "ma";
        await Ui.WaitUntil(() => rig.Palette.Rows.Count > 0);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(250));
        rig.Palette.Query = "mat";
        await Ui.WaitUntil(() => rig.Clock.PendingTimers == 1);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Empty(rig.Search.Queries); // 500 ms in total, but only 250 ms since the last key

        await rig.PauseAsync();
        Assert.Equal(["mat"], rig.Search.Queries);
    }

    [AvaloniaFact]
    public async Task Groups_LabelledLibraryAndOnline()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat", year: 1995), FakeLibrary.Card(2, "Dark", TitleKind.Show, year: 2017));
        rig.Search.Candidates.AddRange([Matrix, Severance]);

        await rig.TypeAsync("he");
        await rig.PauseAsync();

        var rows = rig.Palette.Rows;
        Assert.Equal(["Library", "Heat", "Dark", "Online", "The Matrix", "Severance"], rig.Texts);
        Assert.True(rows[0].IsHeader);
        Assert.False(rows[0].IsSelectable);
        Assert.Equal("1995 · Movie", rows[1].Detail);
        Assert.Equal("2017 · Show", rows[2].Detail);
        Assert.True(rows[3].IsHeader);
        Assert.Equal("1999 · Movie · ★ 8.2", rows[4].Detail);
        Assert.Equal("2022 · Show · ★ 8.4", rows[5].Detail);
        Assert.All(rows.Where(r => !r.IsHeader), r => Assert.True(r.IsSelectable));
    }

    [AvaloniaFact]
    public async Task NothingInTheLibrary_HidesTheLibraryGroup()
    {
        var rig = WithLibrary();
        rig.Search.Candidates.Add(Matrix);

        await rig.TypeAsync("matrix");
        await rig.PauseAsync();

        Assert.Equal(["Online", "The Matrix"], rig.Texts);
    }

    [AvaloniaFact]
    public async Task Offline_OnlineGroupShowsOffline()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"));
        rig.Search.State = OnlineState.Offline;

        await rig.TypeAsync("heat");
        await rig.PauseAsync();

        Assert.Equal(["Library", "Heat", "Online", "Offline"], rig.Texts);
        Assert.False(rig.Palette.Rows[^1].IsSelectable); // a note, not a choice
    }

    [AvaloniaFact]
    public async Task NoKey_OnlineGroupShowsKeyMissing()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"));
        rig.Search.State = OnlineState.NoKey;

        await rig.TypeAsync("heat");
        await rig.PauseAsync();

        Assert.Equal(["Library", "Heat", "Online", "Add a TMDB key"], rig.Texts);
        Assert.True(rig.Palette.Rows[^1].IsSelectable);

        rig.Palette.Rows[^1].OpenCommand.Execute(null);

        Assert.Equal(["settings"], rig.Calls);
        Assert.Equal(1, rig.Closed);
    }

    [AvaloniaFact]
    public async Task KeyRejected_OnlineGroupSaysSo()
    {
        var rig = WithLibrary();
        rig.Search.State = OnlineState.KeyRejected;

        await rig.TypeAsync("heat");
        await rig.PauseAsync();

        Assert.Equal(["Online", "TMDB rejected the key"], rig.Texts);
        Assert.False(rig.Palette.Rows[^1].IsSelectable);
    }

    [AvaloniaFact]
    public async Task TooShort_HidesTheOnlineGroup()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"));
        rig.Search.State = OnlineState.TooShort;

        await rig.TypeAsync("h");
        await rig.PauseAsync();

        Assert.Equal(["Library", "Heat"], rig.Texts);
    }

    [AvaloniaFact]
    public async Task NothingFound_SaysNoMatches_OnceTheOnlineAnswerIsIn()
    {
        var rig = WithLibrary();
        await rig.TypeAsync("zzz");
        Assert.Null(rig.Palette.EmptyText); // TMDB may still have something

        await rig.PauseAsync();

        Assert.Empty(rig.Palette.Rows);
        Assert.Equal("No matches", rig.Palette.EmptyText);
    }

    [AvaloniaFact]
    public async Task OnlineHitAlreadyInTheLibrary_IsAnItemOfTheLibraryGroup_NotShownTwice()
    {
        var rig = WithLibrary(FakeLibrary.Card(7, "The Matrix", year: 1999));
        rig.Search.Candidates.AddRange([Matrix, Severance]);
        rig.Search.LibraryIds[603] = 7; // The Matrix: already listed under Library
        rig.Search.LibraryIds[95396] = 9; // Severance: in the library, but the local search missed it

        await rig.TypeAsync("matrix");
        await rig.PauseAsync();

        Assert.Equal(["Library", "The Matrix", "Severance"], rig.Texts);

        rig.Palette.Rows[2].OpenCommand.Execute(null);

        Assert.Equal(["title:9"], rig.Calls); // the local page, not the online one
    }

    [AvaloniaFact]
    public async Task GreaterThan_SwitchesToCommands()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"));
        rig.Search.CommandList.AddRange([new("rescan", "Rescan library"), new("settings", "Open settings"), new("hidden", "Show hidden items")]);
        await rig.TypeAsync("heat");

        rig.Palette.Query = ">";

        Assert.Equal(["Commands", "Rescan library", "Open settings", "Show hidden items"], rig.Texts);

        rig.Palette.Query = ">set";

        Assert.Equal(["Commands", "Open settings"], rig.Texts);
        Assert.Empty(rig.Search.Queries);
        Assert.Equal(0, rig.Clock.PendingTimers); // no search is waiting on the pause any more
    }

    [AvaloniaFact]
    public async Task GreaterThan_StopsAnOnlineSearchThatWasWaiting()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"));
        rig.Search.Candidates.Add(Matrix);
        await rig.TypeAsync("heat");

        rig.Palette.Query = ">";
        rig.Clock.Advance(Pause);
        Ui.Flush();

        Assert.Empty(rig.Search.Queries);
    }

    [AvaloniaFact]
    public void Commands_RunTheirAction_AndClose()
    {
        var rig = new Rig();
        rig.Search.CommandList.AddRange([new("rescan", "Rescan library"), new("settings", "Open settings"), new("hidden", "Show hidden items")]);

        foreach (var label in new[] { "Rescan library", "Open settings", "Show hidden items" })
        {
            rig.Palette.Query = ">";
            rig.Palette.Rows.Single(r => r.Text == label).OpenCommand.Execute(null);
        }

        Assert.Equal(["rescan", "settings", "hidden"], rig.Calls);
        Assert.Equal(3, rig.Closed);
    }

    [AvaloniaFact]
    public void Commands_NoneMatching_SaysSo()
    {
        var rig = new Rig();
        rig.Search.CommandList.Add(new("settings", "Open settings"));

        rig.Palette.Query = ">xyz";

        Assert.Empty(rig.Palette.Rows);
        Assert.Equal("No commands", rig.Palette.EmptyText);
    }

    [AvaloniaFact]
    public async Task Enter_OpensSelected()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat", year: 1995));
        rig.Search.Candidates.Add(Matrix);
        await rig.TypeAsync("heat");
        await rig.PauseAsync();

        // A library hit opens its title page.
        Assert.Equal("Heat", rig.Palette.Selected?.Text);
        rig.Palette.OpenSelectedCommand.Execute(null);
        Assert.Equal(["title:1"], rig.Calls);
        Assert.Equal(1, rig.Closed);

        // An online hit opens the details page, which says "Not in library".
        rig.Palette.MoveDownCommand.Execute(null);
        Assert.Equal("The Matrix", rig.Palette.Selected?.Text);
        rig.Palette.OpenSelectedCommand.Execute(null);
        Assert.Equal(["title:1", "online:603:Movie"], rig.Calls);
        Assert.Equal(2, rig.Closed);
    }

    [AvaloniaFact]
    public void Enter_WithNothingSelected_DoesNothing()
    {
        var rig = new Rig();

        rig.Palette.OpenSelectedCommand.Execute(null);

        Assert.Empty(rig.Calls);
        Assert.Equal(0, rig.Closed);
    }

    [AvaloniaFact]
    public void Escape_Closes()
    {
        var rig = new Rig();

        rig.Palette.CloseCommand.Execute(null);

        Assert.Equal(1, rig.Closed);
        Assert.Empty(rig.Calls);
    }

    // Escape peels one layer: the text first, then the palette.
    [AvaloniaFact]
    public void Escape_ClearsTheTextFirst_ThenCloses()
    {
        var rig = new Rig();
        rig.Palette.Query = "hea";

        rig.Palette.EscapeCommand.Execute(null);
        Assert.Equal("", rig.Palette.Query);
        Assert.Equal(0, rig.Closed);

        rig.Palette.EscapeCommand.Execute(null);
        Assert.Equal(1, rig.Closed);
    }

    [AvaloniaFact]
    public async Task Arrows_MoveSelection()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"), FakeLibrary.Card(2, "Dark", TitleKind.Show));
        rig.Search.Candidates.Add(Matrix);
        await rig.TypeAsync("he");
        await rig.PauseAsync();
        // Library, Heat, Dark, Online, The Matrix
        Assert.Equal(1, rig.Palette.SelectedIndex); // the first choice, not the header

        rig.Palette.MoveDownCommand.Execute(null);
        Assert.Equal("Dark", rig.Palette.Selected?.Text);

        rig.Palette.MoveDownCommand.Execute(null); // across the group, skipping the "Online" header
        Assert.Equal("The Matrix", rig.Palette.Selected?.Text);

        rig.Palette.MoveDownCommand.Execute(null); // past the end, back to the top
        Assert.Equal("Heat", rig.Palette.Selected?.Text);

        rig.Palette.MoveUpCommand.Execute(null); // and round again
        Assert.Equal("The Matrix", rig.Palette.Selected?.Text);

        rig.Palette.MoveUpCommand.Execute(null);
        Assert.Equal("Dark", rig.Palette.Selected?.Text);
        Assert.Single(rig.Palette.Rows, r => r.IsSelected);
    }

    [AvaloniaFact]
    public async Task Arrows_WithNothingToChoose_Stay()
    {
        var rig = WithLibrary();
        await rig.TypeAsync("zzz");

        rig.Palette.MoveDownCommand.Execute(null);
        rig.Palette.MoveUpCommand.Execute(null);

        Assert.Equal(-1, rig.Palette.SelectedIndex);
    }

    [AvaloniaFact]
    public async Task OnlineArriving_KeepsTheRowTheViewerIsOn()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"), FakeLibrary.Card(2, "Dark"));
        rig.Search.Candidates.Add(Matrix);
        await rig.TypeAsync("he");
        rig.Palette.MoveDownCommand.Execute(null);
        Assert.Equal("Dark", rig.Palette.Selected?.Text);

        await rig.PauseAsync();

        Assert.Equal("Dark", rig.Palette.Selected?.Text);
        Assert.Equal(["Library", "Heat", "Dark", "Online", "The Matrix"], rig.Texts);
    }

    [AvaloniaFact]
    public async Task NewKeystroke_CancelsTheSearchInFlight_AndItsAnswerIsDropped()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"));
        var held = new TaskCompletionSource<SearchResults>();
        CancellationToken heldToken = default;
        rig.Search.OnSearch = (query, ct) =>
        {
            if (query != "he") return Task.FromResult(new SearchResults([], [new OnlineResult(Matrix, null)], OnlineState.Ok));
            heldToken = ct;
            return held.Task;
        };

        await rig.TypeAsync("he");
        rig.Clock.Advance(Pause);
        await Ui.WaitUntil(() => rig.Search.Queries.Count == 1); // the first search is out
        var first = rig.Palette.Settled;

        await rig.TypeAsync("hea");
        Assert.True(heldToken.IsCancellationRequested);
        await rig.PauseAsync();
        Assert.Equal(["Library", "Heat", "Online", "The Matrix"], rig.Texts);

        // The cancelled search answers late. Its answer must not replace the newer one.
        held.SetResult(new SearchResults([], [new OnlineResult(Severance, null)], OnlineState.Ok));
        await Ui.WaitUntil(() => first.IsCompleted);

        Assert.Equal(["Library", "Heat", "Online", "The Matrix"], rig.Texts);
    }

    [AvaloniaFact]
    public async Task ASlowLibraryAnswer_NeverReplacesANewerOne()
    {
        var rig = new Rig();
        var release = new ManualResetEventSlim();
        rig.Library.LocalSearch = query =>
        {
            if (query == "he") release.Wait(TimeSpan.FromSeconds(10)); // the first key's answer is slow
            return [FakeLibrary.Card(query == "he" ? 1 : 2, query == "he" ? "Old" : "New")];
        };

        rig.Palette.Query = "he";
        var slow = rig.Palette.LibraryTask;
        rig.Palette.Query = "hea";
        await Ui.WaitUntil(() => rig.Texts.Contains("New"));
        release.Set();
        await Ui.WaitUntil(() => slow.IsCompleted); // the old answer has arrived, and was dropped

        Assert.Equal(["Library", "New"], rig.Texts);
    }

    [AvaloniaFact]
    public async Task ClearingTheBox_EmptiesThePalette_AndStopsTheSearch()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"));
        await rig.TypeAsync("heat");

        rig.Palette.Query = "";
        rig.Clock.Advance(Pause);
        Ui.Flush();

        Assert.Empty(rig.Palette.Rows);
        Assert.Null(rig.Palette.EmptyText);
        Assert.Empty(rig.Search.Queries);
    }

    [AvaloniaFact]
    public async Task Reset_ClearsTheBoxAndTheResults()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"));
        await rig.TypeAsync("heat");

        rig.Palette.Reset();

        Assert.Equal("", rig.Palette.Query);
        Assert.Empty(rig.Palette.Rows);
        rig.Clock.Advance(Pause);
        Ui.Flush();
        Assert.Empty(rig.Search.Queries);
    }

    [AvaloniaFact]
    public async Task ASearchThatFails_IsLogged_AndLeavesTheLibraryGroup()
    {
        var rig = WithLibrary(FakeLibrary.Card(1, "Heat"));
        rig.Search.OnSearch = (_, _) => throw new InvalidOperationException("boom");

        await rig.TypeAsync("heat");
        rig.Clock.Advance(Pause);
        await Ui.WaitUntil(() => rig.Hub.Recent.Any(e => e.Level == ActivityLevel.Error));

        Assert.Contains(rig.Hub.Recent, e => e.Message.Contains("boom"));
        Assert.Equal(["Library", "Heat"], rig.Texts);
    }
}
