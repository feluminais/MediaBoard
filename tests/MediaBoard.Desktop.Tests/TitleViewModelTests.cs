using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;
using MediaBoard.Desktop.Views;

namespace MediaBoard.Desktop.Tests;

public class TitleViewModelTests
{
    private readonly ActivityHub _hub = new();
    private readonly FakeLibrary _library = new();
    private readonly Titles _t;

    public TitleViewModelTests()
    {
        _hub.SetOnline(true);
        _hub.SetKeyState(KeyState.Ok);
        _t = new Titles(_library, _hub);
    }

    private async Task<TitleViewModel> Loaded(int titleId)
    {
        var page = _t.Page(titleId);
        await page.RefreshAsync();
        return page;
    }

    // Season 1 of show 2: E04 half-way (file 24), E05 not started (file 25), E06 watched (file 26).
    private void AddShow()
    {
        _library.Details[2] = FakeLibrary.Show(2, "Dark",
            FakeLibrary.Season(2, 1,
                FakeLibrary.Episode(204, 1, 4, "Alone", 24, position: 1390),
                FakeLibrary.Episode(205, 1, 5, "Dark Matter", 25),
                FakeLibrary.Episode(206, 1, 6, "Choices", 26, watched: true),
                FakeLibrary.Episode(207, 1, 7, "Nobody has this", null)));
    }

    [AvaloniaFact]
    public async Task PlayButton_Text()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Next[1] = 10;
        Assert.Equal("Play in MPC-HC", (await Loaded(1)).PlayText);

        _library.Details[1] = FakeLibrary.Movie(1, "Klaus", position: 3730);
        Assert.Equal("Resume in MPC-HC, 1:02:10", (await Loaded(1)).PlayText);

        AddShow();
        _library.Next[2] = 24;
        Assert.Equal("Resume S01E04 in MPC-HC, 23:10", (await Loaded(2)).PlayText);

        _library.Next[2] = 25;
        Assert.Equal("Play S01E05 in MPC-HC", (await Loaded(2)).PlayText);
    }

    [AvaloniaFact]
    public async Task PlayButton_NamesTheResolvedPlayer()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Next[1] = 10;
        _t.Player = "mpv";

        Assert.Equal("Play in mpv", (await Loaded(1)).PlayText);
    }

    [AvaloniaFact]
    public async Task PlayButton_WithoutAPlayer_OpensSettings()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Next[1] = 10;
        _t.Player = null;
        var page = await Loaded(1);

        Assert.Equal("Choose a player", page.PlayText);
        Assert.True(page.PlayCommand.CanExecute(null));
        await page.PlayCommand.ExecuteAsync(null);

        Assert.Equal(1, _t.SettingsOpened);
        Assert.Empty(_t.Playback.Played);
    }

    [AvaloniaFact]
    public async Task PlayButton_StartsTheFile_OffTheUiThread_AndIgnoresASecondClickWhileStarting()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Next[1] = 10;
        var page = await Loaded(1);
        var gate = new TaskCompletionSource();
        _t.Playback.PlayGate = gate.Task;
        var uiThread = Environment.CurrentManagedThreadId;

        var first = page.PlayCommand.ExecuteAsync(null);
        await Ui.WaitUntil(() => _t.Playback.Played.Count == 1);
        page.PlayCommand.Execute(null); // the player is still starting
        gate.SetResult();
        await first;

        var played = Assert.Single(_t.Playback.Played);
        Assert.Equal((10, false), (played.FileId, played.FromStart));
        Assert.NotEqual(uiThread, played.ThreadId);
    }

    [AvaloniaFact]
    public async Task Launcher_IgnoresAStartWhileAnotherIsStarting()
    {
        var gate = new TaskCompletionSource();
        _t.Playback.PlayGate = gate.Task;

        var first = _t.Launcher.PlayAsync(10);
        await Ui.WaitUntil(() => _t.Playback.Played.Count == 1);
        await _t.Launcher.PlayAsync(11); // returns at once
        gate.SetResult();
        await first;
        await _t.Launcher.PlayAsync(12); // the guard is open again

        Assert.Equal([10, 12], _t.Playback.Played.Select(p => p.FileId));
    }

    [AvaloniaFact]
    public async Task Launcher_LogsAFailedStart_InsteadOfThrowing()
    {
        _t.Playback.PlayGate = Task.FromException(new IOException("disk gone"));

        await _t.Launcher.PlayAsync(10);

        Assert.Contains(_hub.Recent, e => e.Level == ActivityLevel.Error && e.Message == "Couldn't start playback: disk gone");
    }

    [AvaloniaFact]
    public async Task PlayerName_IsResolvedOncePerPage()
    {
        var calls = 0;
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Next[1] = 10;
        var page = new TitleViewModel(1, _t.Services() with { PlayerName = () => { Interlocked.Increment(ref calls); return "MPC-HC"; } }, _t.Navigation());

        await page.RefreshAsync();
        await page.RefreshAsync();
        await page.RefreshAsync();

        Assert.Equal(1, calls);
    }

    [AvaloniaFact]
    public async Task Page_ShowsPosterDescriptionRatingAndYear()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _t.Posters = Posters.Local([]);
        var page = await Loaded(1);
        await Ui.WaitUntil(() => page.PosterLocalPath is not null);

        Assert.Equal("Klaus", page.Header.Name);
        Assert.Equal("About Klaus", page.Overview);
        Assert.Equal("1999", page.YearText);
        Assert.Equal("\u2605 7.4", page.RatingText);
        Assert.Null(page.Tag);
    }

    [AvaloniaFact]
    public async Task Page_OfShowsRunningYears_ShowsTheRange_AndTagsAnUnidentifiedTitle()
    {
        AddShow();
        _library.Details[3] = FakeLibrary.Movie(3, "x") with { Offline = true };

        Assert.Equal("2015\u20132022", (await Loaded(2)).YearText);
        Assert.Equal("Not identified (offline)", (await Loaded(3)).Tag);
    }

    [AvaloniaFact]
    public async Task Seasons_ShowWatchedState()
    {
        AddShow();
        var page = await Loaded(2);

        var season = Assert.Single(page.Seasons);
        Assert.Equal("Season 1", season.Name);
        var episodes = season.Episodes;
        Assert.Equal(["E04", "E05", "E06", "E07"], episodes.Select(e => e.Label));
        Assert.Equal([false, false, true, false], episodes.Select(e => e.IsWatched));
        Assert.Equal("23:10", episodes[0].StatusText); // half-way
        Assert.True(episodes[0].Progress is > 0 and < 1);
        Assert.Equal("", episodes[1].StatusText);
        Assert.Equal("Watched", episodes[2].StatusText);
        Assert.Equal("No file", episodes[3].StatusText);
        Assert.False(episodes[3].CanPlay);
        Assert.Equal("Mark unwatched", episodes[2].WatchedText);
    }

    [AvaloniaFact]
    public async Task Rename_Inline_UpdatesDisplay()
    {
        AddShow();
        var page = await Loaded(2);

        page.Header.BeginRenameCommand.Execute(null);
        Assert.True(page.Header.IsEditing);
        Assert.Equal("Dark", page.Header.EditText);
        page.Header.EditText = "Dunkel";
        await page.Header.CommitRenameCommand.ExecuteAsync(null);

        Assert.Contains("rename:Title:2:Dunkel", _library.Edits);
        Assert.Equal("Dunkel", page.Header.Name);
        Assert.False(page.Header.IsEditing);

        var season = page.Seasons[0];
        season.BeginRenameCommand.Execute(null);
        season.EditText = "Part one";
        await season.CommitRenameCommand.ExecuteAsync(null);
        Assert.Contains($"rename:Season:{LibraryIds.Season(2, 1)}:Part one", _library.Edits);
        Assert.Equal("Part one", page.Seasons[0].Name);

        var episode = page.Seasons[0].Episodes[1];
        episode.BeginRenameCommand.Execute(null);
        episode.EditText = "Matter";
        await episode.CommitRenameCommand.ExecuteAsync(null);
        Assert.Contains("rename:Episode:205:Matter", _library.Edits);
        Assert.Equal("Matter", page.Seasons[0].Episodes[1].Name);
    }

    [AvaloniaFact]
    public async Task Rename_Empty_ClearsTheOverride_AndCancelChangesNothing()
    {
        AddShow();
        var page = await Loaded(2);

        page.Header.BeginRenameCommand.Execute(null);
        page.Header.EditText = "  ";
        await page.Header.CommitRenameCommand.ExecuteAsync(null);
        Assert.Contains("rename:Title:2:", _library.Edits); // null name: the override goes

        var episode = page.Seasons[0].Episodes[0];
        episode.BeginRenameCommand.Execute(null);
        episode.EditText = "Typed and dropped";
        episode.CancelRenameCommand.Execute(null);
        Assert.False(episode.IsEditing);
        Assert.DoesNotContain(_library.Edits, e => e.Contains("Typed and dropped"));
    }

    [AvaloniaFact]
    public async Task Rename_UnchangedName_DoesNotWrite()
    {
        AddShow();
        var page = await Loaded(2);

        page.Header.BeginRenameCommand.Execute(null);
        await page.Header.CommitRenameCommand.ExecuteAsync(null);

        Assert.Empty(_library.Edits);
    }

    [AvaloniaFact]
    public async Task Hide_RemovesFromGrids()
    {
        _library.TitleCards.Add(FakeLibrary.Card(1, "Klaus"));
        _library.TitleCards.Add(FakeLibrary.Card(5, "Coco"));
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        var grid = new TitleGridViewModel(TitleKind.Movie, _library, _hub, Posters.None(), new PageActions(_ => { }, _ => { }, _ => { }));
        await grid.RefreshAsync();
        Assert.Equal(2, grid.Items.Count);
        var page = await Loaded(1);
        Assert.Equal("Hide", page.HideText);

        await page.ToggleHiddenCommand.ExecuteAsync(null);
        await grid.RefreshAsync();

        Assert.Contains("hidden:Title:1:True", _library.Edits);
        Assert.Equal(["Coco"], grid.Items.Select(c => c.Name));
        Assert.True(page.Header.IsHidden);
        Assert.Equal("Show", page.HideText);

        await page.ToggleHiddenCommand.ExecuteAsync(null);
        Assert.Contains("hidden:Title:1:False", _library.Edits);
    }

    [AvaloniaFact]
    public async Task Hide_Season_AndEpisode()
    {
        AddShow();
        var page = await Loaded(2);

        await page.Seasons[0].ToggleHiddenCommand.ExecuteAsync(null);
        await page.Seasons[0].Episodes[1].ToggleHiddenCommand.ExecuteAsync(null);

        Assert.Contains($"hidden:Season:{LibraryIds.Season(2, 1)}:True", _library.Edits);
        Assert.Contains("hidden:Episode:205:True", _library.Edits);
        Assert.True(page.Seasons[0].IsHidden);
        Assert.Equal("Show", page.Seasons[0].Episodes[1].HideText);
    }

    [AvaloniaFact]
    public async Task FixMatch_OpensSearchPicker()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus", fileId: 10);
        _t.Search.Candidates.Add(new SearchCandidate(11, TitleKind.Movie, "Klaus", null, 2019, 50, 8.1, null, null));
        _t.Search.Candidates.Add(new SearchCandidate(12, TitleKind.Show, "Klaus Show", null, 2001, 5, 6.0, null, null));
        var page = await Loaded(1);

        page.FixMatchCommand.Execute(null);

        var picker = Assert.IsType<FixMatchPickerViewModel>(page.Picker);
        Assert.Equal("Klaus", picker.Query);
        Assert.Equal(MatchFilter.Movies, picker.Filter); // the title's own kind
        await picker.SearchAsync();
        Assert.Equal(["Klaus"], picker.Results.Select(r => r.Name));
        Assert.Equal("2019 \u00b7 Movie \u00b7 8.1", picker.Results[0].Detail);
        picker.Filter = MatchFilter.All;
        Assert.Equal(["Klaus", "Klaus Show"], picker.Results.Select(r => r.Name));

        await picker.PickCommand.ExecuteAsync(picker.Results[1]);

        Assert.Contains("fix:10:12:Show", _library.Edits);
        Assert.Null(page.Picker);
        Assert.Equal(1, _t.Identified); // identification runs for the siblings the fix requeued
    }

    [AvaloniaFact]
    public async Task FixMatch_Cancel_ClosesThePicker_WithoutChangingAnything()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        var page = await Loaded(1);
        page.FixMatchCommand.Execute(null);

        page.Picker!.CancelCommand.Execute(null);

        Assert.Null(page.Picker);
        Assert.Empty(_library.Edits);
    }

    [AvaloniaFact]
    public async Task FixMatch_Searches_AfterATypingPause()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _t.Search.Candidates.Add(new SearchCandidate(11, TitleKind.Movie, "Heat", null, 1995, 50, 8.0, null, null));
        var page = await Loaded(1);
        page.FixMatchCommand.Execute(null);
        var picker = page.Picker!;
        await picker.SearchAsync();
        _t.Search.Queries.Clear();

        picker.Query = "H";
        picker.Query = "Heat";
        await Ui.WaitUntil(() => _t.Search.Queries.Count >= 1);
        await Task.Delay(2 * FixMatchPickerViewModel.TypingPauseMs);
        Ui.Flush();

        Assert.Equal(["Heat"], _t.Search.Queries); // one search, for the final text
    }

    [AvaloniaTheory]
    [InlineData(false, KeyState.Ok, "Connect to the internet to fix this match")]
    [InlineData(true, KeyState.Missing, "Add a TMDB key in Settings")]
    [InlineData(true, KeyState.Rejected, "TMDB rejected the key. Check it in Settings.")]
    public async Task FixMatch_WithoutTmdb_SaysWhy_AndOpensNoPicker(bool online, KeyState key, string message)
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _hub.SetOnline(online);
        _hub.SetKeyState(key);
        var page = await Loaded(1);

        page.FixMatchCommand.Execute(null);

        Assert.Null(page.Picker);
        Assert.Equal(message, page.Notice);
    }

    [AvaloniaFact]
    public async Task FixMatch_WhenTmdbCannotAnswer_ShowsTheReason_AndKeepsTheOldMatch()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.OnFixMatch = _ => throw new MetadataUnavailableException(UnavailableReason.Offline);
        _t.Search.Candidates.Add(new SearchCandidate(11, TitleKind.Movie, "Klaus", null, 2019, 50, 8.1, null, null));
        var page = await Loaded(1);
        page.FixMatchCommand.Execute(null);
        await page.Picker!.SearchAsync();

        await page.Picker.PickCommand.ExecuteAsync(page.Picker.Results[0]);

        Assert.Equal("Connect to the internet to fix this match", page.Notice);
        Assert.Equal(0, _t.Identified);
        Assert.Equal("Klaus", page.Header.Name);
    }

    [AvaloniaTheory]
    [InlineData(OnlineState.Offline, "Connect to the internet to search")]
    [InlineData(OnlineState.NoKey, "Add a TMDB key in Settings")]
    [InlineData(OnlineState.KeyRejected, "TMDB rejected the key. Check it in Settings.")]
    [InlineData(OnlineState.TooShort, "Type to search")]
    public async Task Picker_ExplainsWhyThereAreNoResults(OnlineState state, string message)
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _t.Search.State = state;
        var page = await Loaded(1);
        page.FixMatchCommand.Execute(null);

        await page.Picker!.SearchAsync();

        Assert.Empty(page.Picker.Results);
        Assert.Equal(message, page.Picker.Message);
    }

    [AvaloniaFact]
    public async Task Picker_WithNoMatches_Says()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        var page = await Loaded(1);
        page.FixMatchCommand.Execute(null);

        await page.Picker!.SearchAsync();

        Assert.Equal("No matches", page.Picker.Message);
    }

    private async Task PickFirst(TitleViewModel page, int tmdbId, TitleKind kind)
    {
        _t.Search.Candidates.Clear();
        _t.Search.Candidates.Add(new SearchCandidate(tmdbId, kind, "Better match", null, 2019, 50, 8.1, null, null));
        page.FixMatchCommand.Execute(null);
        page.Picker!.Filter = MatchFilter.All;
        await page.Picker.SearchAsync();
        await page.Picker.PickCommand.ExecuteAsync(page.Picker.Results[0]);
    }

    [AvaloniaFact]
    public async Task FixMatch_MovesToTheNewTitle_WhenTheFileLeftThisOne()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        var page = await Loaded(1);

        await PickFirst(page, 111, TitleKind.Movie);

        Assert.Equal([FakeLibrary.MatchedTitleId(111)], _t.Replaced);
        Assert.Equal(0, _t.WentBack);
    }

    [AvaloniaFact]
    public async Task FixMatch_MovesEveryFileOfAShow()
    {
        AddShow(); // files 24, 25, 26
        var page = await Loaded(2);

        await PickFirst(page, 99, TitleKind.Show);

        Assert.Equal(["fix:24:99:Show", "fix:25:99:Show", "fix:26:99:Show"], _library.Edits);
        Assert.False(_library.Details.ContainsKey(2)); // nothing is left behind on the old title
        Assert.Equal(3, _library.Details[FakeLibrary.MatchedTitleId(99)].Files.Count);
        Assert.Equal([FakeLibrary.MatchedTitleId(99)], _t.Replaced);
        Assert.Equal(1, _t.Identified);
    }

    [AvaloniaFact]
    public async Task FixMatch_MovesEveryFileOfAFlatFolderTitle()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus") with
        {
            Files =
            [
                new FileView(10, "Klaus.mkv", "Klaus.mkv", false, false, 0, 0, false),
                new FileView(11, "Klaus (extended).mkv", "Klaus (extended).mkv", false, false, 0, 0, false),
            ],
        };
        var page = await Loaded(1);

        await PickFirst(page, 99, TitleKind.Movie);

        Assert.Equal(["fix:10:99:Movie", "fix:11:99:Movie"], _library.Edits);
        Assert.False(_library.Details.ContainsKey(1));
    }

    [AvaloniaFact]
    public async Task FixMatch_StopsAtTheFirstFailure_AndSaysHowManyMoved()
    {
        AddShow();
        _library.OnFixMatch = file =>
        {
            if (file == 25) throw new MetadataUnavailableException(UnavailableReason.Offline);
        };
        var page = await Loaded(2);

        await PickFirst(page, 99, TitleKind.Show);

        Assert.Equal(["fix:24:99:Show"], _library.Edits); // 25 failed before it moved, 26 was never tried
        Assert.Equal("Connect to the internet to fix this match. Moved 1 of 3 files.", page.Notice);
        Assert.Equal(1, _t.Identified); // what moved is identified
        Assert.True(_library.Details.ContainsKey(2)); // the title still has files, so the page stays
        Assert.Empty(_t.Replaced);
        Assert.Equal(0, _t.WentBack);
    }

    [AvaloniaFact]
    public async Task NotAMovieOrShow_MarksEveryFile_AfterConfirm_AndGoesBack()
    {
        AddShow();
        var page = await Loaded(2);

        _t.ConfirmNotMedia = false;
        await page.NotMediaCommand.ExecuteAsync(null);
        Assert.Empty(_library.Edits);
        Assert.Equal(0, _t.WentBack);

        _t.ConfirmNotMedia = true;
        await page.NotMediaCommand.ExecuteAsync(null);

        Assert.Equal(["Dark", "Dark"], _t.NotMediaAsked);
        Assert.Equal(["notmedia:24", "notmedia:25", "notmedia:26"], _library.Edits);
        Assert.Equal(1, _t.WentBack);
    }

    [AvaloniaFact]
    public async Task TitleView_OffersNotAMovieOrShow()
    {
        AddShow();
        var view = new TitleView { DataContext = await Loaded(2) };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Show();
        Ui.Flush();

        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();

        Assert.Contains("Not a movie or show", buttons);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RenameBox_AfterARefresh_KeepsTheTextAndDoesNotSelectIt()
    {
        AddShow();
        var page = await Loaded(2);
        var view = new TitleView { DataContext = page };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Show();
        Ui.Flush();
        List<string?> Texts() => view.GetVisualDescendants().OfType<RenameBox>().Where(b => b.IsEffectivelyVisible).Select(b => b.Text).ToList();
        RenameBox Box() => view.GetVisualDescendants().OfType<RenameBox>().Single(b => b.IsEffectivelyVisible);

        page.Seasons[0].Episodes[1].BeginRenameCommand.Execute(null);
        Ui.Flush();
        window.UpdateLayout();
        Ui.Flush();
        Assert.Equal("Dark Matter", Box().SelectedText); // a fresh edit selects the whole name, to type over it

        Box().Text = "Dark Matter, extended";
        Box().CaretIndex = Box().Text!.Length;
        await page.RefreshAsync(); // a scan changed the library while the viewer was typing
        Ui.Flush();
        window.UpdateLayout();
        Ui.Flush();

        Assert.Equal(["Dark Matter, extended"], Texts());
        Assert.Equal("", Box().SelectedText);
        Assert.Equal("Dark Matter, extended".Length, Box().CaretIndex);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MarkWatched_TogglesEpisode()
    {
        AddShow();
        var page = await Loaded(2);
        var episode = page.Seasons[0].Episodes[1];
        Assert.Equal("Mark watched", episode.WatchedText);

        await episode.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.Contains("watched:25:True", _library.Edits);
        Assert.True(page.Seasons[0].Episodes[1].IsWatched);

        await page.Seasons[0].Episodes[1].ToggleWatchedCommand.ExecuteAsync(null);

        Assert.Contains("watched:25:False", _library.Edits);
        Assert.False(page.Seasons[0].Episodes[1].IsWatched);
    }

    [AvaloniaFact]
    public async Task MarkWatched_WholeTitle_MarksEveryFile_ThenUnmarksThem()
    {
        AddShow();
        var page = await Loaded(2);
        Assert.Equal("Mark watched", page.WatchedText);

        await page.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.Equal(["watched:24:True", "watched:25:True"], _library.Edits); // 26 already was
        Assert.Equal("Mark unwatched", page.WatchedText);

        _library.Edits.Clear();
        await page.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.Equal(["watched:24:False", "watched:25:False", "watched:26:False"], _library.Edits);
    }

    [AvaloniaFact]
    public async Task MarkWatched_Movie_UsesItsFile()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus", fileId: 10);
        var page = await Loaded(1);

        await page.ToggleWatchedCommand.ExecuteAsync(null);

        Assert.Equal(["watched:10:True"], _library.Edits);
    }

    private static DeletePreview PreviewOf(params DeleteFile[] files) => new(1, "Klaus", files);

    [AvaloniaFact]
    public async Task Delete_ShowsFilesAndSeedingWarning_RequiresConfirm()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Preview = PreviewOf(new DeleteFile(10, @"D:\Movies\Klaus.mkv", 4_000_000_000), new DeleteFile(11, @"D:\Movies\Klaus (2).mkv", 1_000));
        DeletePreview? shown = null;
        var answer = false;
        _t.ConfirmDelete = preview => { shown = preview; return Task.FromResult(answer); };
        var page = await Loaded(1);

        await page.DeleteCommand.ExecuteAsync(null);

        Assert.Same(_library.Preview, shown);
        Assert.DoesNotContain("delete", _library.Edits); // declined: nothing was sent to the Recycle Bin

        answer = true;
        await page.DeleteCommand.ExecuteAsync(null);
        Assert.Contains("delete", _library.Edits);
    }

    [AvaloniaFact]
    public void ConfirmDeleteDialog_ListsEveryPath_AndWarnsAboutSeeding()
    {
        var preview = PreviewOf(new DeleteFile(10, @"D:\Movies\Klaus.mkv", 4_000_000_000), new DeleteFile(11, @"D:\Movies\Klaus (2).mkv", 1_000));
        var dialog = new ConfirmDeleteDialog(preview);
        dialog.Show();

        var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        var buttons = dialog.GetVisualDescendants().OfType<Button>().Select(b => b.Content).ToList();

        Assert.Contains(@"D:\Movies\Klaus.mkv", texts);
        Assert.Contains(@"D:\Movies\Klaus (2).mkv", texts);
        Assert.Contains("Torrent clients seeding these files will lose them.", texts);
        Assert.Contains("These files go to the Recycle Bin.", texts);
        Assert.Contains("Delete 2 files of Klaus?", texts);
        Assert.DoesNotContain(texts, t => t is not null && t.Contains("deleted permanently"));
        Assert.Contains("Delete", buttons);
        Assert.Contains("Cancel", buttons);
        dialog.Close();
    }

    [AvaloniaFact]
    public void ConfirmDeleteDialog_WarnsWhenAFileMayBeDeletedPermanently()
    {
        var preview = PreviewOf(new DeleteFile(10, @"\\nas\movies\Klaus.mkv", 1, MayDeletePermanently: true));
        var dialog = new ConfirmDeleteDialog(preview);
        dialog.Show();

        var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

        Assert.Contains("Delete 1 file of Klaus?", texts);
        Assert.Contains("Files on a network or removable drive may be deleted permanently.", texts);
        dialog.Close();
    }

    [AvaloniaTheory]
    [InlineData("Delete", true)]
    [InlineData("Cancel", false)]
    public async Task ConfirmDeleteDialog_ReturnsTheButtonPressed(string button, bool expected)
    {
        var owner = new Window();
        owner.Show();
        var answer = new Dialogs { Owner = owner }.ConfirmDeleteAsync(PreviewOf(new DeleteFile(10, @"D:\Klaus.mkv", 1)));
        Ui.Flush();

        var dialog = Assert.Single(owner.OwnedWindows);
        dialog.GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == button)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(expected, await answer);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Delete_ShowsTheResult_IncludingFilesThatStayed()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Preview = PreviewOf(new DeleteFile(10, @"D:\Movies\Klaus.mkv", 1), new DeleteFile(11, @"E:\Klaus 2.mkv", 1, true));
        _library.DeleteResult = new DeleteResult([@"D:\Movies\Klaus.mkv"], [new DeleteFailure(@"E:\Klaus 2.mkv", "Windows asked to delete it for good and you said no.", Declined: true)]);
        var page = await Loaded(1);

        await page.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["Deleted 1 file.", "Kept Klaus 2.mkv: Windows asked to delete it for good and you said no."], page.DeleteSummary);
        Assert.Equal(0, _t.WentBack); // a file is still in the library
        page.DismissSummaryCommand.Execute(null);
        Assert.Empty(page.DeleteSummary);
    }

    [AvaloniaFact]
    public async Task Delete_GoesBack_WhenNoFileIsLeft()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Preview = PreviewOf(new DeleteFile(10, @"D:\Movies\Klaus.mkv", 1));
        _library.DeleteResult = new DeleteResult([@"D:\Movies\Klaus.mkv"], []);
        var page = await Loaded(1);
        var deleting = page.DeleteCommand.ExecuteAsync(null);
        _library.Details.Remove(1); // the rows went with the files
        await deleting;

        Assert.Equal(1, _t.WentBack);
        Assert.Contains(_hub.Recent, e => e.Message == "Deleted 1 file of Klaus.");
    }

    [AvaloniaFact]
    public async Task Delete_WithNothingOnDisk_SaysSo()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus", missing: true);
        var page = await Loaded(1);

        await page.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["No files on disk to delete."], page.DeleteSummary);
        Assert.DoesNotContain("delete", _library.Edits);
    }

    [AvaloniaFact]
    public async Task Missing_ShowsFileMissing_PlayDisabledWithReason()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus", missing: true); // Next[1] stays null: nothing can be played
        var page = await Loaded(1);

        Assert.Equal("Play in MPC-HC", page.PlayText);
        Assert.False(page.CanPlay);
        Assert.False(page.PlayCommand.CanExecute(null));
        Assert.Equal("File missing", page.PlayReason);
        Assert.True(page.HasPlayReason);

        AddShow();
        _library.Details[2] = FakeLibrary.Show(2, "Dark",
            FakeLibrary.Season(2, 1, FakeLibrary.Episode(204, 1, 4, "Alone", 24, missing: true), FakeLibrary.Episode(205, 1, 5, "Matter", 25)));
        _library.Next[2] = 25;
        var show = await Loaded(2);
        var gone = show.Seasons[0].Episodes[0];
        Assert.Equal("File missing", gone.StatusText);
        Assert.False(gone.CanPlay);
        Assert.True(show.Seasons[0].Episodes[1].CanPlay);
        Assert.True(show.CanPlay); // another episode can play
    }

    [AvaloniaFact]
    public async Task NothingPlayable_ButNotMissing_SaysNothingToPlay()
    {
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        var page = await Loaded(1);

        Assert.False(page.CanPlay);
        Assert.Equal("Nothing to play", page.PlayReason);
    }

    [AvaloniaFact]
    public async Task LaunchOnlyPlayer_AsksFinished()
    {
        var asked = new List<string>();
        _t.AskFinished = title => { asked.Add(title); return Task.FromResult<bool?>(true); };
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Next[1] = 10;
        var page = await Loaded(1);
        await page.PlayCommand.ExecuteAsync(null);

        await Task.Run(() => _t.Playback.RaiseAskFinished(10, "Klaus")); // the player closed: a background thread says so
        await Ui.WaitUntil(() => _t.Playback.Answers.Count == 1);

        Assert.Equal(["Klaus"], asked);
        Assert.Equal((10, true), _t.Playback.Answers[0]);
    }

    [AvaloniaFact]
    public async Task LaunchOnlyPlayer_AnswerNo_IsRecorded_AndDismissingRecordsNothing()
    {
        var answers = new Queue<bool?>([false, null]);
        _t.AskFinished = _ => Task.FromResult(answers.Dequeue());
        _library.Details[1] = FakeLibrary.Movie(1, "Klaus");
        _library.Next[1] = 10;
        var page = await Loaded(1);
        await page.PlayCommand.ExecuteAsync(null);

        await Task.Run(() => _t.Playback.RaiseAskFinished(10, "Klaus"));
        await Ui.WaitUntil(() => _t.Playback.Answers.Count == 1);
        await Task.Run(() => _t.Playback.RaiseAskFinished(10, "Klaus"));
        await Ui.WaitUntil(() => answers.Count == 0);
        Ui.Flush();

        Assert.Equal([(10, false)], _t.Playback.Answers);
    }

    [AvaloniaFact]
    public async Task LaunchOnlyPlayer_AnswerGoesToTheFileTheServiceNamed_NotTheLastOneClicked()
    {
        _t.AskFinished = _ => Task.FromResult<bool?>(true);

        // File 12 never started (missing, or no player); file 10's player closes meanwhile.
        await _t.Launcher.PlayAsync(10);
        await _t.Launcher.PlayAsync(12);
        await Task.Run(() => _t.Playback.RaiseAskFinished(10, "Klaus"));
        await Ui.WaitUntil(() => _t.Playback.Answers.Count == 1);

        Assert.Equal([(10, true)], _t.Playback.Answers);
    }

    [AvaloniaFact]
    public void FinishedPromptDialog_AsksAboutTheTitle_WithYesAndNo()
    {
        var dialog = new FinishedPromptDialog("Klaus");
        dialog.Show();

        var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        var buttons = dialog.GetVisualDescendants().OfType<Button>().Select(b => b.Content).ToList();

        Assert.Contains("Did you finish Klaus?", texts);
        Assert.Contains("Yes", buttons);
        Assert.Contains("No", buttons);
        dialog.Close();
    }

    [AvaloniaTheory]
    [InlineData("Yes", true)]
    [InlineData("No", false)]
    public async Task FinishedPrompt_ReturnsTheAnswer_AndNullWhenClosed(string button, bool expected)
    {
        var owner = new Window();
        owner.Show();
        var dialogs = new Dialogs { Owner = owner };

        var answer = dialogs.AskFinishedAsync("Klaus");
        Ui.Flush();
        Assert.Single(owner.OwnedWindows).GetVisualDescendants().OfType<Button>().Single(b => (string?)b.Content == button)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(expected, await answer);

        var dismissed = dialogs.AskFinishedAsync("Klaus");
        Ui.Flush();
        Assert.Single(owner.OwnedWindows).Close();
        Assert.Null(await dismissed);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task FinishedPrompt_WithoutAWindow_RecordsNothing()
    {
        Assert.Null(await new Dialogs().AskFinishedAsync("Klaus"));
        Assert.False(await new Dialogs().ConfirmDeleteAsync(PreviewOf()));
    }

    private static readonly TitleDetails Online = new(603, TitleKind.Movie, "The Matrix", 1999, null, "A hacker learns.", 8.2, "/m.jpg", 136, []);

    [AvaloniaFact]
    public async Task OnlineTitle_ShowsNotInLibraryTag_NoPlayButton()
    {
        _t.Search.Details = Online;
        _t.Posters = Posters.Local([]);
        var page = _t.OnlinePage(603, TitleKind.Movie);
        await page.RefreshAsync();

        Assert.True(page.IsOnline);
        Assert.Equal("Not in library", page.Tag);
        Assert.Equal("The Matrix", page.Header.Name);
        Assert.Equal("1999", page.YearText);
        Assert.Equal("\u2605 8.2", page.RatingText);
        Assert.Equal("A hacker learns.", page.Overview);
        Assert.False(page.ShowActions); // no Play, no edits, no delete
        Assert.Null(page.OfflineMessage);
        await Ui.WaitUntil(() => page.PosterLocalPath is not null);

        var view = new TitleView { DataContext = page };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Ui.Flush();
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();

        Assert.Contains("Not in library", texts);
        Assert.Contains("The Matrix", texts);
        Assert.DoesNotContain(buttons, b => b is not null && (b.StartsWith("Play") || b.StartsWith("Resume")));
        Assert.DoesNotContain("Delete from disk", buttons);
        Assert.DoesNotContain("Fix match", buttons);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(KeyState.Ok, "Offline")]
    [InlineData(KeyState.Missing, "Add a TMDB key in Settings")]
    public async Task OnlineTitle_WhenTmdbCannotAnswer_SaysOffline(KeyState key, string message)
    {
        _hub.SetKeyState(key);
        _t.Search.Details = null;
        var page = _t.OnlinePage(603, TitleKind.Movie);

        await page.RefreshAsync();

        Assert.Equal(message, page.OfflineMessage);
        Assert.False(page.ShowActions);
        Assert.Equal("Not in library", page.Tag);
    }

    [AvaloniaFact]
    public async Task OnlineTitle_IsNotFetchedAgain_WhenTheLibraryChanges()
    {
        _t.Search.Details = Online;
        var page = _t.OnlinePage(603, TitleKind.Movie);
        await page.RefreshAsync();
        _t.Search.Details = Online with { Name = "Changed" };

        await page.RefreshAsync();

        Assert.Equal("The Matrix", page.Header.Name);
    }

    [AvaloniaFact]
    public async Task TitleGone_SaysSo()
    {
        var page = await Loaded(99);

        Assert.True(page.NotFound);
        Assert.Equal("This title isn't in the library anymore.", page.NotFoundText);
        Assert.False(page.ShowActions);
    }

    [AvaloniaFact]
    public async Task EditingText_SurvivesARefresh()
    {
        AddShow();
        var page = await Loaded(2);
        var episode = page.Seasons[0].Episodes[1];
        episode.BeginRenameCommand.Execute(null);
        episode.EditText = "Half typed";

        await page.RefreshAsync();

        var again = page.Seasons[0].Episodes[1];
        Assert.True(again.IsEditing);
        Assert.Equal("Half typed", again.EditText);
    }

    [AvaloniaFact]
    public async Task TitleView_ShowsPlayButton_Seasons_AndActions()
    {
        AddShow();
        _library.Next[2] = 24;
        var page = await Loaded(2);
        var view = new TitleView { DataContext = page };
        var window = new Window { Width = 1000, Height = 800, Content = view };
        window.Show();
        Ui.Flush();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();

        Assert.Contains("Dark", texts);
        Assert.Contains("About Dark", texts);
        Assert.Contains("Season 1", texts);
        Assert.Contains("Dark Matter", texts);
        Assert.Contains("Watched", texts);
        Assert.Contains("Resume S01E04 in MPC-HC, 23:10", buttons);
        Assert.Contains("Fix match", buttons);
        Assert.Contains("Delete from disk", buttons);
        Assert.Contains("Mark watched", buttons);
        Assert.DoesNotContain("Not in library", texts);
        window.Close();
    }
}
