using Avalonia.Headless.XUnit;
using MediaBoard.Core.Activity;
using MediaBoard.Core.Library;
using MediaBoard.Core.Metadata;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Core.Storage;
using MediaBoard.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MediaBoard.Desktop.Tests;

public sealed class FoldersViewModelTests : IDisposable
{
    private readonly ShellStack _stack = new();
    private readonly List<string> _confirmations = [];
    private string? _picked;
    private bool _confirmAnswer = true;

    public void Dispose() => _stack.Dispose();

    private FoldersViewModel Create() => new(
        _stack.Library, _stack.Actions, _stack.Hub,
        pickFolder: () => Task.FromResult(_picked),
        confirm: text =>
        {
            _confirmations.Add(text);
            return Task.FromResult(_confirmAnswer);
        });

    // Runs the command, waits for the scan and identification it started, and reloads the list.
    private async Task AddAsync(FoldersViewModel vm, string? path)
    {
        _picked = path;
        await vm.AddFolderCommand.ExecuteAsync(null);
        await Settle(vm);
    }

    private async Task Settle(FoldersViewModel vm)
    {
        await _stack.Actions.WhenIdleAsync();
        await vm.RefreshAsync();
        Ui.Flush();
    }

    private async Task<FoldersViewModel> LoadedAsync()
    {
        var vm = Create();
        await vm.RefreshAsync();
        Ui.Flush();
        return vm;
    }

    [AvaloniaFact]
    public async Task EmptyList_SaysWhy()
    {
        var vm = await LoadedAsync();

        Assert.True(vm.ShowEmpty);
        Assert.Empty(vm.Folders);
    }

    [AvaloniaFact]
    public async Task AddFolder_ScansImmediately()
    {
        var root = _stack.MakeLibrary("Movies", "Heat (1995).mkv", "Alien (1979).mkv");
        var vm = await LoadedAsync();

        await AddAsync(vm, root);

        var row = Assert.Single(_stack.Library.Folders());
        Assert.Equal(root, row.Path);
        Assert.Equal(2, row.FileCount); // scanned without a restart or a manual rescan
        var item = Assert.Single(vm.Folders);
        Assert.Equal("2 files", item.Summary);
        Assert.False(vm.ShowEmpty);
        Assert.Null(vm.Message);
    }

    [AvaloniaFact]
    public async Task AddFolder_CancelledPicker_AddsNothing()
    {
        var vm = await LoadedAsync();

        await AddAsync(vm, null);

        Assert.Empty(_stack.Library.Folders());
        Assert.Null(vm.Message);
    }

    [AvaloniaFact]
    public async Task AddFolder_SamePathTwice_IsRefused_IgnoringCase()
    {
        var root = _stack.MakeLibrary("Movies", "Heat (1995).mkv");
        var vm = await LoadedAsync();
        await AddAsync(vm, root);

        await AddAsync(vm, root.ToUpperInvariant());

        Assert.Single(_stack.Library.Folders());
        Assert.Equal("That folder is already in the library.", vm.Message);
    }

    [AvaloniaFact]
    public async Task AddFolder_InsideALibraryFolder_IsRefused()
    {
        var root = _stack.MakeLibrary("Shows", @"Lost\Season 1\Lost.S01E01.mkv");
        var vm = await LoadedAsync();
        await AddAsync(vm, root);

        await AddAsync(vm, Path.Combine(root, "Lost"));

        Assert.Single(_stack.Library.Folders());
        Assert.Contains(root, vm.Message);
        Assert.Contains("inside", vm.Message);
    }

    [AvaloniaFact]
    public async Task AddFolder_ContainingALibraryFolder_IsRefused()
    {
        var inner = _stack.MakeLibrary(@"All\Shows", "Lost.S01E01.mkv");
        var outer = Directory.GetParent(inner)!.FullName;
        var vm = await LoadedAsync();
        await AddAsync(vm, inner);

        await AddAsync(vm, outer);

        Assert.Single(_stack.Library.Folders());
        Assert.Contains(inner, vm.Message);
        Assert.Contains("contains", vm.Message);
    }

    [AvaloniaFact]
    public async Task AddFolder_MissingFolder_IsRefused()
    {
        var vm = await LoadedAsync();

        await AddAsync(vm, Path.Combine(Path.GetTempPath(), "mediaboard-no-such-folder"));

        Assert.Empty(_stack.Library.Folders());
        Assert.Equal("Can't find that folder.", vm.Message);
    }

    [AvaloniaFact]
    public async Task RemoveFolder_AsksConfirm_RemovesRowsOnly()
    {
        var root = _stack.MakeLibrary("Movies", "Heat (1995).mkv", "Alien (1979).mkv");
        var vm = await LoadedAsync();
        await AddAsync(vm, root);
        var item = Assert.Single(vm.Folders);

        // Declined: nothing happens.
        _confirmAnswer = false;
        await vm.RemoveCommand.ExecuteAsync(item);
        await Settle(vm);
        Assert.Equal(2, Assert.Single(_stack.Library.Folders()).FileCount);

        // Accepted: the folder and its rows go, the files stay.
        _confirmAnswer = true;
        await vm.RemoveCommand.ExecuteAsync(item);
        await Settle(vm);

        Assert.Equal(2, _confirmations.Count);
        Assert.Equal(
            "Remove \"Movies\" from the library? Your files aren't touched, but MediaBoard forgets its watch progress and " +
            "your edits, such as renames and hidden items. To keep those, use Disable instead, or Locate… if the folder moved.",
            _confirmations[0]);
        Assert.Empty(_stack.Library.Folders());
        Assert.Empty(vm.Folders);
        Assert.True(vm.ShowEmpty);
        Assert.True(File.Exists(Path.Combine(root, "Heat (1995).mkv")));
        Assert.True(File.Exists(Path.Combine(root, "Alien (1979).mkv")));
    }

    [AvaloniaFact]
    public async Task Disable_KeepsTheFolderListed_HidesItsTitles_AndEnableBringsThemBack()
    {
        var root = _stack.MakeLibrary("Movies", "Heat (1995).mkv");
        var vm = await LoadedAsync();
        await AddAsync(vm, root);
        var item = Assert.Single(vm.Folders);
        Assert.Equal("Disable", item.ToggleLabel);
        _stack.LinkMovie(root, "Heat (1995).mkv", "Heat", 949);
        await vm.RefreshAsync();
        Assert.NotEmpty(_stack.Library.Titles(TitleKind.Movie, TitleSort.Name, true));
        Assert.NotEmpty(_stack.Library.RecentlyAdded(10));

        await vm.ToggleEnabledCommand.ExecuteAsync(item);
        await Settle(vm);

        var off = Assert.Single(vm.Folders);
        Assert.False(off.IsEnabled);
        Assert.Equal("Enable", off.ToggleLabel);
        Assert.Equal("Disabled", off.Summary);
        Assert.False(Assert.Single(_stack.Library.Folders()).Enabled);
        Assert.Empty(_stack.Library.Titles(TitleKind.Movie, TitleSort.Name, true));
        Assert.Empty(_stack.Library.RecentlyAdded(10));

        File.WriteAllBytes(Path.Combine(root, "Alien (1979).mkv"), []);
        await vm.ToggleEnabledCommand.ExecuteAsync(off);
        await Settle(vm);

        var row = Assert.Single(_stack.Library.Folders());
        Assert.True(row.Enabled);
        Assert.Equal(2, row.FileCount); // enabling scans at once
        Assert.NotEmpty(_stack.Library.Titles(TitleKind.Movie, TitleSort.Name, true));
        Assert.NotEmpty(_stack.Library.RecentlyAdded(10));
    }

    [AvaloniaFact]
    public async Task Repoint_UpdatesPath()
    {
        var root = _stack.MakeLibrary("Movies", "Heat (1995).mkv", "Alien (1979).mkv");
        var moved = Path.Combine(Path.GetDirectoryName(root)!, "Movies (moved)");
        var vm = await LoadedAsync();
        await AddAsync(vm, root);
        Directory.Move(root, moved); // the drive got another letter, say
        await Settle(vm);
        var item = Assert.Single(vm.Folders);
        Assert.True(item.IsUnavailable);

        _picked = moved;
        await vm.RepointCommand.ExecuteAsync(item);
        await Settle(vm);

        var row = Assert.Single(_stack.Library.Folders());
        Assert.Equal(moved, row.Path);
        Assert.False(row.Unavailable);
        Assert.Equal(2, row.FileCount); // the same file rows, found again
        Assert.False(Assert.Single(vm.Folders).IsUnavailable);
    }

    [AvaloniaFact]
    public async Task Repoint_OntoAnotherLibraryFolder_IsRefused()
    {
        var first = _stack.MakeLibrary("Movies", "Heat (1995).mkv");
        var second = _stack.MakeLibrary("Shows", "Lost.S01E01.mkv");
        var vm = await LoadedAsync();
        await AddAsync(vm, first);
        await AddAsync(vm, second);

        _picked = second;
        await vm.RepointCommand.ExecuteAsync(vm.Folders[0]);
        await Settle(vm);

        Assert.Equal(first, _stack.Library.Folders()[0].Path);
        Assert.Equal("That folder is already in the library.", vm.Message);
    }

    [AvaloniaFact]
    public async Task IgnoredFoldersListed_AsIgnoredNoVideo()
    {
        var root = _stack.MakeLibrary("Downloads", "Heat (1995).mkv", @"GTA IV\setup.exe", @"Photoscape\readme.txt");
        var vm = await LoadedAsync();

        await AddAsync(vm, root);

        var item = Assert.Single(vm.Folders);
        Assert.Equal(["GTA IV", "Photoscape"], item.Ignored.Select(i => i.Name).Order());
        Assert.All(item.Ignored, i => Assert.Equal("Ignored: no video", i.Label));
        Assert.True(item.HasIgnored);
    }

    [AvaloniaFact]
    public async Task UnavailableFolder_ShowsRepointAction()
    {
        var movies = _stack.MakeLibrary("Movies", "Heat (1995).mkv");
        var shows = _stack.MakeLibrary("Shows", "Lost.S01E01.mkv");
        var vm = await LoadedAsync();
        await AddAsync(vm, movies);
        await AddAsync(vm, shows);
        Directory.Delete(shows, recursive: true); // an unplugged drive looks the same
        await Settle(vm);

        var gone = vm.Folders.Single(f => f.Path == shows);
        var here = vm.Folders.Single(f => f.Path == movies);

        Assert.True(gone.IsUnavailable);
        Assert.True(gone.ShowRepoint);
        Assert.Contains("Unavailable", gone.Summary);
        Assert.False(here.ShowRepoint);
    }

    [AvaloniaFact]
    public async Task AddFolder_WhoseScanFails_ShowsScanFailedOnItsRow()
    {
        var root = _stack.MakeLibrary("Movies", "Heat (1995).mkv");
        var vm = await LoadedAsync();
        // Every insert into files fails, as a full disk would make it.
        using (var connection = _stack.Services.GetRequiredService<LibraryDatabase>().Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_scan BEFORE INSERT ON files BEGIN SELECT RAISE(ABORT, 'disk I/O error'); END;";
            command.ExecuteNonQuery();
        }

        await AddAsync(vm, root);

        var item = Assert.Single(vm.Folders);
        Assert.Equal("Scan failed", item.Summary); // not "No videos found"
        Assert.Equal(["Movies"], _stack.Hub.Current.FailedScans);
        Assert.Contains(_stack.Hub.Recent, e => e.Level == ActivityLevel.Error && e.Message.Contains("disk I/O error"));
    }

    [AvaloniaFact]
    public async Task Refresh_RunsTheQueryOffTheUiThread()
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var queryThreads = new List<int>();
        var library = new FakeLibrary { OnQuery = _ => { lock (queryThreads) queryThreads.Add(Environment.CurrentManagedThreadId); } };
        var vm = new FoldersViewModel(library, _stack.Actions, _stack.Hub, () => Task.FromResult<string?>(null), _ => Task.FromResult(true));

        await vm.RefreshAsync();

        lock (queryThreads)
        {
            Assert.NotEmpty(queryThreads);
            Assert.DoesNotContain(uiThread, queryThreads);
        }
    }
}
