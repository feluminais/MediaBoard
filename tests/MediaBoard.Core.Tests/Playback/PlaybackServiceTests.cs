using Dapper;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Players;
using MediaBoard.Core.Players.MpcHc;
using MediaBoard.Core.Tests.Players;
using MediaBoard.Core.Playback;
using MediaBoard.Core.Settings;
using MediaBoard.Core.Storage;
using MediaBoard.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Playback;

public sealed class PlaybackServiceTests : IDisposable
{
    private const double Hour = 6000; // a 100-minute film, in seconds

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly TempLibrary _library = new();
    private readonly LibraryDatabase _db;
    private readonly FileStore _files;
    private readonly ProgressStore _progress;
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
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _settings.Save(new AppSettings { DefaultPlayerId = "mpv" });
        _folderId = new FolderStore(_db).Add(_library.Root).Id;

        var detector = new PlayerDetector(_ => _playerInstalled, () => [], _hub);
        _service = new PlaybackService(_files, _progress, detector, _settings, _hub,
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
        _progress.Save(id, 600, Hour);

        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        var start = Assert.Single(Session.Starts);
        Assert.Equal(_library.PathOf("Movie (2020).mkv"), start.FilePath);
        Assert.Equal(TimeSpan.FromSeconds(600), start.StartAt);
    }

    [Fact]
    public async Task Watched_StartsFromZero()
    {
        var id = AddFile("Movie (2020).mkv");
        _progress.Save(id, 600, Hour);
        _progress.SetWatched(id, true);

        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        Assert.Null(Assert.Single(Session.Starts).StartAt);
    }

    [Fact]
    public async Task FromStart_IgnoresSavedPosition()
    {
        var id = AddFile("Movie (2020).mkv");
        _progress.Save(id, 600, Hour);

        await _service.PlayAsync(id, fromStart: true, CancellationToken.None);

        Assert.Null(Assert.Single(Session.Starts).StartAt);
    }

    [Fact]
    public async Task NothingSaved_StartsFromZero()
    {
        var id = AddFile("Movie (2020).mkv");

        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

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
        Assert.False(_progress.Get(id)!.Watched);
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
        Assert.False(_progress.Get(id)!.Watched);

        Session.Raise(new PlaybackEvent.Ended());

        Assert.True(_progress.Get(id)!.Watched);
    }

    [Fact]
    public async Task Ended_WithoutKnownPosition_MarksWatched()
    {
        var id = await Play("Movie (2020).mkv");

        Session.Raise(new PlaybackEvent.Ended());

        Assert.True(_progress.Get(id)!.Watched);
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
        Assert.False(_progress.Get(id)!.Watched);
    }

    [Fact]
    public async Task NinetyPercent_MarksWatched()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));

        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(0.9 * Hour)));

        Assert.True(_progress.Get(id)!.Watched);
    }

    [Fact]
    public async Task EightyNinePercent_NotWatched()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));

        Session.Raise(new PlaybackEvent.Position(TimeSpan.FromSeconds(0.89 * Hour)));
        Session.Raise(new PlaybackEvent.Closed());

        Assert.False(_progress.Get(id)!.Watched);
    }

    [Fact]
    public async Task Failed_DoesNotOverwriteProgress()
    {
        var id = AddFile("Movie (2020).mkv");
        _progress.Save(id, 500, Hour);
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
        _progress.Save(id, 500, Hour);
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
        Assert.False(_progress.Get(id)!.Watched);
    }

    [Fact]
    public async Task PlayerName_ShownWhilePlaying()
    {
        await Play("Movie (2020).mkv");
        Assert.Equal("mpv", _hub.Current.PlayerName);

        Session.Raise(new PlaybackEvent.Closed());

        Assert.Null(_hub.Current.PlayerName);
    }

    [Fact]
    public async Task MissingFile_LogsFileMissing_DoesNotStartPlayer()
    {
        var id = AddFile("Movie (2020).mkv", missing: true);

        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        AssertFileMissing("Movie (2020).mkv");
    }

    [Fact]
    public async Task FileGoneFromDisk_LogsFileMissing_DoesNotStartPlayer()
    {
        var id = AddFile("Movie (2020).mkv");
        _library.Delete("Movie (2020).mkv");

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
        Assert.Null(_progress.Get(id));

        _service.AnswerFinished(id, finished: true);
        Assert.True(_progress.Get(id)!.Watched);

        _service.AnswerFinished(id, finished: false);
        Assert.False(_progress.Get(id)!.Watched);
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
        Assert.Empty(_progress.WatchedEpisodeIds());
        await _service.PlayAsync(id, fromStart: false, CancellationToken.None);

        Session.Raise(new PlaybackEvent.Ended());

        Assert.Equal(episodes, _progress.WatchedEpisodeIds());
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
        var service = new PlaybackService(_files, _progress, new PlayerDetector(_ => true, () => [], _hub), _settings, _hub,
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
    public async Task MpcHcMovesToAnotherFile_SessionEnds_PlayerKeepsRunning()
    {
        var transport = new LoopTransport();
        var process = new FakePlayerProcess();
        var service = new PlaybackService(_files, _progress, new PlayerDetector(_ => true, () => [], _hub), _settings, _hub,
            player => new MpcHcSession(player, () => transport, _ => process));
        var id = AddFile("Movie (2020).mkv");
        await service.PlayAsync(id, fromStart: false, CancellationToken.None);
        transport.Receive(MpcApi.CMD_CONNECT, "4660");
        transport.Receive(MpcApi.CMD_STATE, "2");
        transport.Receive(MpcApi.CMD_NOWPLAYING, $"Movie|||{_library.PathOf("Movie (2020).mkv")}|{Hour}.000");
        transport.Receive(MpcApi.CMD_NOTIFYSEEK, "300.000");

        transport.Receive(MpcApi.CMD_NOWPLAYING, $"Next|||{_library.PathOf("Next (2021).mkv")}|{Hour}.000"); // Next in MPC-HC

        await Waiting.Until(() => process.Disposed);
        Assert.False(process.CloseRequested);
        Assert.False(process.Killed);
        Assert.Null(_hub.Current.PlayerName);
        AssertProgress(id, 300, Hour);
    }

    [Fact]
    public async Task Stop_ClosingThePlayer_SavesAndClosesIt()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Position = TimeSpan.FromMinutes(12);
        Assert.Equal("mpv", _service.CurrentPlayer);

        await _service.StopAsync(closePlayer: true, CancellationToken.None);

        AssertProgress(id, 720, Hour);
        Assert.True(Session.Disposed);
        Assert.False(Session.Detached);
        Assert.Null(_service.CurrentPlayer);
        Assert.Null(_hub.Current.PlayerName);
    }

    [Fact]
    public async Task Stop_LeavingThePlayerOpen_SavesAndDetaches()
    {
        var id = await Play("Movie (2020).mkv");
        Session.Raise(new PlaybackEvent.Loaded(TimeSpan.FromSeconds(Hour)));
        Session.Position = TimeSpan.FromMinutes(12);

        await _service.StopAsync(closePlayer: false, CancellationToken.None);

        AssertProgress(id, 720, Hour);
        Assert.True(Session.Detached);
        Assert.True(Session.Disposed);
        Assert.Null(_service.CurrentPlayer);
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
        var service = new PlaybackService(_files, _progress, new PlayerDetector(_ => true, () => [], _hub), _settings, _hub,
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

    /// <summary>An MPC-HC message window that only records what is sent; the test plays MPC-HC's part.</summary>
    private sealed class LoopTransport : IMpcTransport
    {
        public nint HostHandle => 4096;

        public event Action<uint, string>? Received;

        public void Receive(uint cmd, string payload) => Received?.Invoke(cmd, payload);

        public void Send(nint target, uint cmd, string payload)
        {
        }

        public void Dispose()
        {
        }
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
        var saved = _progress.Get(fileId);
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
