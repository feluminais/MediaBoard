using Dapper;
using CozyCouch.Core.Activity;
using CozyCouch.Core.Players;
using CozyCouch.Core.Players.MpcHc;
using CozyCouch.Core.Tests.Players;
using CozyCouch.Core.Playback;
using CozyCouch.Core.Settings;
using CozyCouch.Core.Storage;
using CozyCouch.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace CozyCouch.Core.Tests.Playback;

public sealed class PlaybackServiceTests : IDisposable
{
    private const double Hour = 6000; // a 100-minute film, in seconds
    private const int Owner = LibraryDatabase.OwnerId;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cozycouch-test-" + Guid.NewGuid().ToString("N"));
    private readonly TempLibrary _library = new();
    private readonly LibraryDatabase _db;
    private readonly FileStore _files;
    private readonly ProgressStore _progress;
    private readonly LocalPlaybackSource _source;
    private readonly ActivityHub _hub = new();
    private readonly SettingsStore _settings;
    private readonly PlaybackService _service;
    private readonly int _folderId;
    private bool _playerInstalled = true;

    public PlaybackServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _files = new FileStore(_db);
        _progress = new ProgressStore(_db);
        _source = new LocalPlaybackSource(_files, _progress);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _settings.Save(new AppSettings { DefaultPlayerId = "mpv" });
        _folderId = new FolderStore(_db).Add(_library.Root).Id;

        var detector = new PlayerDetector(_ => _playerInstalled, () => [], _hub);
        _service = new PlaybackService(_source, _source, detector, _settings, _hub,
            player =>
            {
                var session = new FakePlayerSession(player.TracksProgress);
                Sessions.Add(session);
                return session;
            });
    }

    private List<FakePlayerSession> Sessions { get; } = [];

    private FakePlayerSession Session => Sessions[^1];

    public void Dispose()
    {
        _library.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Resume_PassesSavedPosition()
    {
        var id = AddFile("Movie (2020).mkv");
        _progress.Save(Owner, id, 600, Hour);

        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        var start = Assert.Single(Session.Starts);
        Assert.Equal(_library.PathOf("Movie (2020).mkv"), start.FilePath);
        Assert.Equal(TimeSpan.FromSeconds(600), start.StartAt);
    }

    [Theory]
    [InlineData(true, true, false)] // watched: the saved position no longer counts
    [InlineData(true, false, true)] // asked to start over
    [InlineData(false, false, false)] // nothing saved
    public async Task Play_StartsFromZero(bool saved, bool watched, bool fromStart)
    {
        var id = AddFile("Movie (2020).mkv");
        if (saved) _progress.Save(Owner, id, 600, Hour);
        if (watched) _progress.SetWatched(Owner, id, true);

        await _service.PlayAsync(id, fromStart, CancellationToken.None);

        Assert.Null(Assert.Single(Session.Starts).StartAt);
    }

    [Fact]
    public async Task PositionEvents_Persist()
    {
        var id = await Play("Movie (2020).mkv");

        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(5)));
        AssertProgress(id, 300, Hour);

        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(7)));
        Session.Raise(new PlaybackEvent.Paused());
        AssertProgress(id, 420, Hour);

        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(8)));
        Session.Raise(new PlaybackEvent.Closed());
        AssertProgress(id, 480, Hour);
        Assert.False(_progress.Get(Owner, id)!.Watched);
    }

    [Fact]
    public async Task PlayingBeforeLoaded_StillSaves()
    {
        // mpv raises Playing before Loaded.
        var id = await Play("Movie (2020).mkv");

        Session.Raise(new PlaybackEvent.Playing());
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(5)));
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Paused());

        AssertProgress(id, 300, Hour);
    }

    [Fact]
    public async Task Ended_MarksWatched()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        // Short of 90%, but inside the last 10% + 15 s that the end of a film is allowed to skip (credits).
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(5390)));
        Assert.False(_progress.Get(Owner, id)!.Watched);

        Session.Raise(new PlaybackEvent.Ended());

        Assert.True(_progress.Get(Owner, id)!.Watched);
    }

    // A scan can drop the row while the player runs. The owner gets a message, and the player's event thread carries on.
    [Fact]
    public async Task Ended_OnADeletedFile_SaysProgressWasNotSaved()
    {
        var id = await Play("Movie (2020).mkv");
        using (var connection = _db.Open())
        {
            connection.Execute("DELETE FROM files WHERE id = @id", new { id });
        }

        Session.Raise(new PlaybackEvent.Ended());

        Assert.Single(_hub.Recent, e => e.Message == "Couldn't save progress");
    }

    [Fact]
    public async Task Ended_WithoutKnownPosition_MarksWatched()
    {
        var id = await Play("Movie (2020).mkv");

        Session.Raise(new PlaybackEvent.Ended());

        Assert.True(_progress.Get(Owner, id)!.Watched);
    }

    [Fact]
    public async Task Ended_AfterAnEarlyStop_SavesThePositionOnly()
    {
        // VLC raises Ended when the user presses Stop.
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(600)));

        Session.Raise(new PlaybackEvent.Ended());

        AssertProgress(id, 600, Hour);
        Assert.False(_progress.Get(Owner, id)!.Watched);
    }

    [Fact]
    public async Task NinetyPercent_MarksWatched()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));

        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(0.9 * Hour)));

        Assert.True(_progress.Get(Owner, id)!.Watched);
    }

    [Fact]
    public async Task EightyNinePercent_NotWatched()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));

        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(0.89 * Hour)));
        Session.Raise(new PlaybackEvent.Closed());

        Assert.False(_progress.Get(Owner, id)!.Watched);
    }

    [Fact]
    public async Task Failed_DoesNotOverwriteProgress()
    {
        var id = AddFile("Movie (2020).mkv");
        _progress.Save(Owner, id, 500, Hour);
        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.Zero));
        Session.Raise(new PlaybackEvent.Failed("MPC-HC could not open the file"));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(30))); // nothing follows Failed, but be safe

        AssertProgress(id, 500, Hour);
        var entry = Assert.Single(_hub.Recent);
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.Equal("MPC-HC could not open the file", entry.Message);
        Assert.Null(_hub.Current.PlayerName);
    }

    [Fact]
    public async Task ResumeSeekNotLandedYet_DoesNotOverwriteSavedPosition()
    {
        var id = AddFile("Movie (2020).mkv");
        _progress.Save(Owner, id, 500, Hour);
        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.Zero));
        Session.Raise(new PlaybackEvent.Paused());
        AssertProgress(id, 500, Hour);

        // Once the seek lands and the player reports real positions, they are saved.
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(502)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(0.5))); // the viewer jumped back to the start
        Session.Raise(new PlaybackEvent.Closed());
        AssertProgress(id, 0.5, Hour);
    }

    [Fact]
    public async Task Closed_IsFinal()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(5)));
        Session.Raise(new PlaybackEvent.Closed());

        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(9)));
        Session.Raise(new PlaybackEvent.Ended());

        AssertProgress(id, 300, 0);
        Assert.False(_progress.Get(Owner, id)!.Watched);
    }

    [Fact]
    public async Task PlayerName_ShownWhilePlaying()
    {
        await Play("Movie (2020).mkv");
        Assert.Equal("mpv", _hub.Current.PlayerName);

        Session.Raise(new PlaybackEvent.Closed());

        Assert.Null(_hub.Current.PlayerName);
    }

    [Theory]
    [InlineData(false)] // flagged missing by the last scan
    [InlineData(true)] // deleted since the last scan
    public async Task MissingFile_LogsFileMissing_DoesNotStartPlayer(bool goneFromDisk)
    {
        var id = AddFile("Movie (2020).mkv", missing: !goneFromDisk);
        if (goneFromDisk) _library.Delete("Movie (2020).mkv");

        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        AssertFileMissing("Movie (2020).mkv");
    }

    [Fact]
    public async Task UnknownFile_LogsFileMissing_DoesNotStartPlayer()
    {
        await _service.PlayAsync(999, fromStart: false, CancellationToken.None);

        var entry = Assert.Single(_hub.Recent);
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.StartsWith("File missing", entry.Message);
        Assert.Empty(Sessions);
    }

    [Fact]
    public async Task NoPlayer_LogsPlayerNotFound()
    {
        var id = AddFile("Movie (2020).mkv");
        _playerInstalled = false;

        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        Assert.Contains(_hub.Recent, e => e.Level == ActivityLevel.Warning && e.Message == "Player not found: mpv");
        Assert.Empty(Sessions);
        Assert.Null(_hub.Current.PlayerName);
    }

    [Fact]
    public async Task LaunchOnly_AsksFinished()
    {
        _settings.Save(new AppSettings { DefaultPlayerId = "custom", CustomPlayerPath = @"C:\Tools\viewer.exe" });
        var id = AddFile("Movie (2020).mkv");
        var asked = new List<(int FileId, string Title)>();
        _service.AskFinished += (file, title) => asked.Add((file, title));
        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);
        Assert.Empty(asked);

        Session.Raise(new PlaybackEvent.Closed());

        Assert.Equal([(id, "Movie")], asked);
        Assert.Null(_hub.Current.PlayerName);
        Assert.Null(_progress.Get(Owner, id));

        _service.AnswerFinished(id, finished: true);
        Assert.True(_progress.Get(Owner, id)!.Watched);

        _service.AnswerFinished(id, finished: false);
        Assert.False(_progress.Get(Owner, id)!.Watched);
    }

    [Fact]
    public async Task LaunchOnly_AfterAFileThatCouldNotStart_AsksAboutTheFileThatWasPlaying()
    {
        _settings.Save(new AppSettings { DefaultPlayerId = "custom", CustomPlayerPath = @"C:\Tools\viewer.exe" });
        var playing = AddFile("Movie (2020).mkv");
        var missing = AddFile("Other (2021).mkv", missing: true);
        var asked = new List<(int FileId, string Title)>();
        _service.AskFinished += (file, title) => asked.Add((file, title));
        await _service.PlayAsync(playing, fromStart: false, CancellationToken.None);

        await _service.PlayAsync(missing, fromStart: false, CancellationToken.None); // returns without starting: the first player stays

        Session.Raise(new PlaybackEvent.Closed());

        Assert.Equal([(playing, "Movie")], asked);
    }

    [Fact]
    public async Task TrackingPlayer_DoesNotAskFinished()
    {
        var asked = new List<(int, string)>();
        _service.AskFinished += (file, title) => asked.Add((file, title));
        await Play("Movie (2020).mkv");

        Session.Raise(new PlaybackEvent.Closed());

        Assert.Empty(asked);
    }

    [Fact]
    public async Task MultiEpisodeFile_MarksAllEpisodes()
    {
        var id = AddFile("Show S01E01-E02.mkv");
        var episodes = LinkEpisodes(id, 2);
        Assert.Empty(_progress.WatchedEpisodeIds(Owner));
        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        Session.Raise(new PlaybackEvent.Ended());

        Assert.Equal(episodes, _progress.WatchedEpisodeIds(Owner));
    }

    [Fact]
    public async Task SecondPlay_SavesAndClosesThePreviousSession()
    {
        var first = AddFile("First.mkv");
        var second = AddFile("Second.mkv");
        await _service.PlayAsync(first, fromStart: false, CancellationToken.None);
        var firstSession = Session;
        firstSession.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        firstSession.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(5)));
        firstSession.Position = TimeSpan.FromMinutes(6);

        await _service.PlayAsync(second, fromStart: false, CancellationToken.None);

        Assert.True(firstSession.Disposed);
        AssertProgress(first, 360, Hour);
        Assert.Equal(2, Sessions.Count);
        Assert.Single(Session.Starts);

        // The old session is final, and the status bar keeps showing the new one.
        firstSession.Raise(new PlaybackEvent.Closed());
        firstSession.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(50)));
        AssertProgress(first, 360, Hour);
        Assert.Equal("mpv", _hub.Current.PlayerName);
    }

    [Fact]
    public async Task StartThrows_LogsAndDoesNotThrow()
    {
        var id = AddFile("Movie (2020).mkv");
        var service = new PlaybackService(_source, _source, new PlayerDetector(_ => true, () => [], _hub), _settings, _hub,
            _ => new FakePlayerSession { StartFailure = new IOException("boom") });

        await service.PlayAsync(id, fromStart: false, CancellationToken.None);

        Assert.Contains(_hub.Recent, e => e.Level == ActivityLevel.Warning && e.Message == "Couldn't start mpv");
        Assert.Null(_hub.Current.PlayerName);
    }

    [Fact]
    public async Task DisposeService_SavesTheOpenSession()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Position = TimeSpan.FromMinutes(12);

        await _service.DisposeAsync();

        AssertProgress(id, 720, Hour);
        Assert.True(Session.Disposed);
    }

    [Fact]
    public async Task MpcHcMovesToAnotherFile_FollowsIt_AndKeepsTheFirstOnesProgress()
    {
        var transport = new FakeTransport();
        var process = new FakePlayerProcess();
        var service = new PlaybackService(_source, _source, new PlayerDetector(_ => true, () => [], _hub), _settings, _hub,
            player => new MpcHcSession(player, () => transport, _ => process));
        var id = AddFile("Movie (2020).mkv");
        await service.PlayAsync(id, fromStart: false, CancellationToken.None);
        transport.Receive(MpcApi.CMD_CONNECT, "4660");
        transport.Receive(MpcApi.CMD_STATE, "2");
        transport.Receive(MpcApi.CMD_NOWPLAYING, $"Movie|||{_library.PathOf("Movie (2020).mkv")}|{Hour}.000");
        transport.Receive(MpcApi.CMD_NOTIFYSEEK, "300.000");

        var next = AddFile("Next (2021).mkv");
        transport.Receive(MpcApi.CMD_NOWPLAYING, $"Next|||{_library.PathOf("Next (2021).mkv")}|{Hour}.000"); // Next in MPC-HC
        transport.Receive(MpcApi.CMD_NOTIFYSEEK, "120.000");

        await Waiting.Until(() => _progress.Get(Owner, next) is not null);
        Assert.False(process.CloseRequested);
        Assert.Equal("mpv", _hub.Current.PlayerName);
        AssertProgress(id, 300, Hour);
        AssertProgress(next, 120, Hour);
    }

    [Fact]
    public async Task FileChanged_KeepsTheLeftEpisodeWhereItWas_AndFollowsTheNextOne()
    {
        var first = await Play("First.mkv");
        var second = AddFile("Second.mkv");
        var changes = 0;
        _service.ProgressChanged += () => changes++;
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(Hour * 0.6)));

        Session.Raise(new PlaybackEvent.FileChanged(_library.PathOf("Second.mkv")));
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour / 2)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(2)));

        AssertProgress(first, Hour * 0.6, Hour);
        Assert.False(_progress.Get(Owner, first)!.Watched);
        AssertProgress(second, 120, Hour / 2);
        Assert.Equal(1, changes);

        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(5)));
        Session.Raise(new PlaybackEvent.Closed());
        AssertProgress(second, 300, Hour / 2);
        AssertProgress(first, Hour * 0.6, Hour);
    }

    [Fact]
    public async Task FileChanged_AfterTheFirstEnded_LeavesItWatchedAndTheNextNot()
    {
        var first = await Play("First.mkv");
        var second = AddFile("Second.mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(5390)));
        Session.Raise(new PlaybackEvent.Ended());

        Session.Raise(new PlaybackEvent.FileChanged(_library.PathOf("Second.mkv")));
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(1)));

        Assert.True(_progress.Get(Owner, first)!.Watched);
        Assert.False(_progress.Get(Owner, second)!.Watched);
    }

    [Fact]
    public async Task FileChanged_ToTheSameFile_ChangesNothing()
    {
        var id = await Play("First.mkv");
        var changes = 0;
        _service.ProgressChanged += () => changes++;
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(5)));

        Session.Raise(new PlaybackEvent.FileChanged(_library.PathOf("first.MKV"))); // Windows ignores case
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(6)));

        AssertProgress(id, 360, Hour);
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"D:\Elsewhere\Not In Library.mkv")]
    public async Task FileChanged_ToNothingOrToAFileOutsideTheLibrary_StopsSaving_UntilALibraryFileComes(string? path)
    {
        var first = await Play("First.mkv");
        var second = AddFile("Second.mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(20)));

        Session.Raise(new PlaybackEvent.FileChanged(path));
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(100)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(50)));
        Session.Raise(new PlaybackEvent.Ended());

        AssertProgress(first, 1200, Hour);
        Assert.Null(_progress.Get(Owner, second));
        Assert.Equal("mpv", _service.CurrentPlayer);

        Session.Raise(new PlaybackEvent.FileChanged(_library.PathOf("Second.mkv")));
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(3)));

        AssertProgress(second, 180, Hour);
    }

    [Fact]
    public async Task FileChanged_ToAnEpisodeWithSavedProgress_KeepsItUntilThePlayerPassesIt()
    {
        await Play("First.mkv");
        var second = AddFile("Second.mkv");
        _progress.Save(Owner, second, 1800, Hour);

        Session.Raise(new PlaybackEvent.FileChanged(_library.PathOf("Second.mkv")));
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(0.5)));
        AssertProgress(second, 1800, Hour);

        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(30)));
        AssertProgress(second, 30, Hour);
    }

    [Fact]
    public async Task Attach_FollowsTheFileThePlayerHasOpen_AndSavesItsProgress()
    {
        var id = AddFile("First.mkv");
        var session = new FakeAttachableSession("mpv")
        {
            RaiseOnAttach = new PlaybackEvent.FileChanged(_library.PathOf("First.mkv")),
        };

        Assert.True(await _service.AttachAsync(session, CancellationToken.None));
        Assert.Equal("mpv", _service.CurrentPlayer);
        Assert.Equal("mpv", _hub.Current.PlayerName);

        session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(10)));
        AssertProgress(id, 600, Hour);

        session.Raise(new PlaybackEvent.Closed());

        Assert.Null(_service.CurrentPlayer);
        await Waiting.Until(() => session.Disposed);
    }

    [Fact]
    public async Task Attach_ThenTheViewerMovesOn_SavesBothEpisodes()
    {
        var first = AddFile("First.mkv");
        var second = AddFile("Second.mkv");
        var session = new FakeAttachableSession { RaiseOnAttach = new PlaybackEvent.FileChanged(_library.PathOf("First.mkv")) };
        await _service.AttachAsync(session, CancellationToken.None);
        session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(Hour * 0.6)));

        session.Raise(new PlaybackEvent.FileChanged(_library.PathOf("Second.mkv")));
        session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(4)));

        AssertProgress(first, Hour * 0.6, Hour);
        AssertProgress(second, 240, Hour);
    }

    [Fact]
    public async Task Attach_PlayerOpenWithAFileOutsideTheLibrary_SavesNothing_UntilALibraryFileComes()
    {
        var id = AddFile("First.mkv");
        var session = new FakeAttachableSession { RaiseOnAttach = new PlaybackEvent.FileChanged(@"D:\Elsewhere\Other.mkv") };
        await _service.AttachAsync(session, CancellationToken.None);
        session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(10)));
        Assert.Null(_progress.Get(Owner, id));

        session.Raise(new PlaybackEvent.FileChanged(_library.PathOf("First.mkv")));
        session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(2)));

        AssertProgress(id, 120, Hour);
    }

    [Theory]
    [InlineData(false)] // no player there to attach to
    [InlineData(true)] // the attach itself throws
    public async Task Attach_Fails_ReturnsFalse_AndDisposesTheSession(bool attachThrows)
    {
        var session = attachThrows
            ? new FakeAttachableSession { AttachFailure = new InvalidOperationException("boom") }
            : new FakeAttachableSession(answers: false);

        Assert.False(await _service.AttachAsync(session, CancellationToken.None));

        Assert.True(session.Disposed);
        Assert.Null(_service.CurrentPlayer);
        Assert.Null(_hub.Current.PlayerName);
    }

    [Fact]
    public async Task Attach_WhileAPlayerIsFollowed_ReturnsFalse_WithoutTouchingIt()
    {
        await Play("First.mkv");
        var session = new FakeAttachableSession();

        Assert.False(await _service.AttachAsync(session, CancellationToken.None));

        Assert.Equal(0, session.AttachCalls);
        Assert.True(session.Disposed);
        Assert.Equal("mpv", _service.CurrentPlayer);
        Assert.False(Session.Disposed);
    }

    [Fact]
    public async Task Play_WhileAttached_SavesThePositionAndLetsGoOfThePlayer()
    {
        var id = AddFile("First.mkv");
        var other = AddFile("Other.mkv");
        var session = new FakeAttachableSession { RaiseOnAttach = new PlaybackEvent.FileChanged(_library.PathOf("First.mkv")) };
        await _service.AttachAsync(session, CancellationToken.None);
        session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        session.Position = TimeSpan.FromMinutes(30);

        await _service.PlayAsync(other, fromStart: false, CancellationToken.None);

        AssertProgress(id, 1800, Hour);
        Assert.True(session.Disposed);
        Assert.Single(Session.Starts);
    }

    [Fact]
    public async Task Stop_AfterAFileChange_SavesTheNewFilesPosition()
    {
        var first = await Play("First.mkv");
        var second = AddFile("Second.mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(10)));
        Session.Raise(new PlaybackEvent.FileChanged(_library.PathOf("Second.mkv")));
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Position = TimeSpan.FromMinutes(15);

        await _service.StopAsync(closePlayer: false, CancellationToken.None);

        AssertProgress(first, 600, Hour);
        AssertProgress(second, 900, Hour);
    }

    [Theory]
    [InlineData(true)] // closes the player
    [InlineData(false)] // leaves it open
    public async Task Stop_SavesThePosition_ThenClosesOrDetachesThePlayer(bool closePlayer)
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Position = TimeSpan.FromMinutes(12);
        Assert.Equal("mpv", _service.CurrentPlayer);

        await _service.StopAsync(closePlayer, CancellationToken.None);

        AssertProgress(id, 720, Hour);
        Assert.True(Session.Disposed);
        Assert.Equal(!closePlayer, Session.Detached);
        Assert.Null(_service.CurrentPlayer);
        Assert.Null(_hub.Current.PlayerName);
    }

    [Fact]
    public async Task SavePosition_KeepsTheSessionGoing()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Position = TimeSpan.FromMinutes(12);

        await _service.SavePositionAsync(CancellationToken.None);

        AssertProgress(id, 720, Hour);
        Assert.False(Session.Disposed);
        Assert.Equal("mpv", _service.CurrentPlayer);
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(13)));
        AssertProgress(id, 780, Hour);
    }

    [Fact]
    public async Task NothingPlaying_HasNoCurrentPlayer_AndStopDoesNothing()
    {
        Assert.Null(_service.CurrentPlayer);

        await _service.SavePositionAsync(CancellationToken.None);
        await _service.StopAsync(closePlayer: false, CancellationToken.None);

        Assert.Empty(Sessions);
    }

    [Fact]
    public async Task SecondPlay_ThatCannotCreateItsSession_ClearsThePlayerName()
    {
        var first = AddFile("First.mkv");
        var second = AddFile("Second.mkv");
        var calls = 0;
        var service = new PlaybackService(_source, _source, new PlayerDetector(_ => true, () => [], _hub), _settings, _hub,
            _ => ++calls == 1 ? new FakePlayerSession() : throw new ArgumentException("bad player"));
        await service.PlayAsync(first, fromStart: false, CancellationToken.None);
        Assert.Equal("mpv", _hub.Current.PlayerName);

        await service.PlayAsync(second, fromStart: false, CancellationToken.None);

        Assert.Null(_hub.Current.PlayerName);
        Assert.Null(service.CurrentPlayer);
    }

    [Fact]
    public async Task ProgressChanged_WhenASessionEnds()
    {
        var changes = 0;
        _service.ProgressChanged += () => Interlocked.Increment(ref changes);
        await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(5)));

        Session.Raise(new PlaybackEvent.Closed());

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ProgressChanged_WhenAFileBecomesWatched()
    {
        var changes = 0;
        _service.ProgressChanged += () => Interlocked.Increment(ref changes);
        await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(5)));
        Assert.Equal(0, changes);

        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(Hour * 0.95)));

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ProgressChanged_WhenAStopSavesTheLastPosition()
    {
        var changes = 0;
        _service.ProgressChanged += () => Interlocked.Increment(ref changes);
        await Play("Movie (2020).mkv");
        Session.Position = TimeSpan.FromMinutes(12);

        await _service.StopAsync(closePlayer: true, CancellationToken.None);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void ProgressChanged_WhenTheViewerAnswersFinished()
    {
        var id = AddFile("Movie (2020).mkv");
        var changes = 0;
        _service.ProgressChanged += () => Interlocked.Increment(ref changes);

        _service.AnswerFinished(id, finished: true);

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Replay_SameFile_ResumesFromPositionTheStopSaved()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Position = TimeSpan.FromMinutes(12);

        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(12), Assert.Single(Session.Starts).StartAt);
    }

    [Fact]
    public async Task Replay_StopCrossesWatchedThreshold_StartsFromBeginning()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromMinutes(10)));
        Session.Position = TimeSpan.FromSeconds(Hour * 0.95);

        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        Assert.True(_progress.Get(Owner, id)!.Watched);
        Assert.Null(Assert.Single(Session.Starts).StartAt);
    }

    private async Task<int> Play(string relPath)
    {
        var id = AddFile(relPath);
        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);
        return id;
    }

    private int AddFile(string relPath, bool missing = false)
    {
        _library.File(relPath);
        const string json =
            """{"Kind":"Movie","Title":"Movie","Year":2020,"Season":null,"Episodes":[],"EpisodeTitles":[],"ShowFolder":null,"TmdbId":null,"ImdbId":null}""";
        using var connection = _db.Open();
        return connection.ExecuteScalar<int>(
            """
            INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status)
            VALUES (@folderId, @relPath, 1, 1, '2026-01-01', @missing, @json, 'matched');
            SELECT last_insert_rowid()
            """,
            new { folderId = _folderId, relPath, missing = missing ? 1 : 0, json });
    }

    private List<int> LinkEpisodes(int fileId, int count)
    {
        using var connection = _db.Open();
        connection.Execute("INSERT INTO titles(kind, name) VALUES ('show', 'Show')");
        var titleId = connection.ExecuteScalar<int>("SELECT last_insert_rowid()");
        var ids = new List<int>();
        for (var number = 1; number <= count; number++)
        {
            connection.Execute("INSERT INTO episodes(title_id, season, number) VALUES (@titleId, 1, @number)", new { titleId, number });
            var episodeId = connection.ExecuteScalar<int>("SELECT last_insert_rowid()");
            connection.Execute("INSERT INTO file_episodes(file_id, episode_id) VALUES (@fileId, @episodeId)", new { fileId, episodeId });
            ids.Add(episodeId);
        }

        return ids;
    }

    private void AssertProgress(int fileId, double position, double duration)
    {
        var saved = _progress.Get(Owner, fileId);
        Assert.NotNull(saved);
        Assert.Equal(position, saved.PositionS, 3);
        Assert.Equal(duration, saved.DurationS, 3);
    }

    private void AssertFileMissing(string name)
    {
        var entry = Assert.Single(_hub.Recent);
        Assert.Equal(ActivityLevel.Warning, entry.Level);
        Assert.Equal($"File missing: {name}", entry.Message);
        Assert.Equal("Reconnect the drive or rescan the folder", entry.Hint);
        Assert.Empty(Sessions);
        Assert.Null(_hub.Current.PlayerName);
    }
}
