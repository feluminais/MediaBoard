using System.Data;
using Dapper;
using MediaBoard.Core.Accounts;
using MediaBoard.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MediaBoard.Core.Tests.Accounts;

public sealed class AccountStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryDatabase _db;
    private readonly AccountStore _store;

    public AccountStoreTests()
    {
        _db = new LibraryDatabase(Path.Combine(_dir, "library.db"));
        _store = new AccountStore(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private int AddFolder(string path)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO folders(path, enabled, added_at) VALUES (@path, 1, '2026-01-01'); SELECT last_insert_rowid()", new { path });
    }

    private int AddFile(int folderId, string relPath)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<int>(
            "INSERT INTO files(folder_id, rel_path, size, mtime, first_seen, missing, parsed_json, status) " +
            "VALUES (@folderId, @relPath, 1, 1, '2026-01-01', 0, '{}', 'matched'); SELECT last_insert_rowid()", new { folderId, relPath });
    }

    [Fact]
    public void Owner_IsAccountOne_WithAllFolders()
    {
        var owner = _store.Get(LibraryDatabase.OwnerId)!;

        Assert.Equal("Owner", owner.Name);
        Assert.Equal(AccountRole.Owner, owner.Role);
        Assert.Null(owner.FolderIds);
        Assert.False(owner.HasPassword);
        Assert.Contains(_store.All(), a => a.Id == LibraryDatabase.OwnerId);
    }

    [Fact]
    public void Create_StoresAViewer()
    {
        var folder = AddFolder("X:\\Media");

        var account = _store.Create("Anna", [folder]);

        Assert.Equal(AccountRole.Viewer, account.Role);
        Assert.Equal([folder], account.FolderIds);
        Assert.False(account.Disabled);
        Assert.Null(account.LastSeen);
        Assert.Equal("Anna", _store.ByName("anna")!.Name);
    }

    [Fact]
    public void Create_NameIsCaseInsensitiveUnique()
    {
        _store.Create("Anna", null);

        Assert.Throws<DuplicateNameException>(() => _store.Create("ANNA", null));
        Assert.Throws<DuplicateNameException>(() => _store.Create("owner", null));
        Assert.Equal(2, _store.All().Count);
    }

    [Fact]
    public void Create_CyrillicNameIsCaseInsensitiveUnique()
    {
        _store.Create("Анна", null);

        Assert.Throws<DuplicateNameException>(() => _store.Create("АННА", null));
        Assert.Throws<DuplicateNameException>(() => _store.Create("  анна ", null));
        Assert.Equal(2, _store.All().Count);
    }

    [Fact]
    public void Create_RefusesEmptyAndTooLongNames()
    {
        Assert.Throws<ArgumentException>(() => _store.Create("   ", null));
        Assert.Throws<ArgumentException>(() => _store.Create(new string('a', AccountStore.MaxNameLength + 1), null));
        Assert.Equal(AccountStore.MaxNameLength, _store.Create(new string('b', AccountStore.MaxNameLength), null).Name.Length);
    }

    [Fact]
    public void ByName_FindsCyrillicInAnotherCase()
    {
        var created = _store.Create("Анна", null);

        Assert.Equal(created.Id, _store.ByName("АННА")!.Id);
        Assert.Null(_store.ByName("Борис"));
    }

    [Fact]
    public void ByName_TrimsSpaces()
    {
        var created = _store.Create("Anna", null);

        Assert.Equal(created.Id, _store.ByName("  anna ")!.Id);
    }

    [Fact]
    public void SetFolders_RefusesOwner()
    {
        Assert.Throws<InvalidOperationException>(() => _store.SetFolders(LibraryDatabase.OwnerId, []));

        Assert.Null(_store.Get(LibraryDatabase.OwnerId)!.FolderIds);
    }

    [Fact]
    public void SetDisabled_RefusesOwner()
    {
        Assert.Throws<InvalidOperationException>(() => _store.SetDisabled(LibraryDatabase.OwnerId, true));

        Assert.False(_store.Get(LibraryDatabase.OwnerId)!.Disabled);
    }

    [Fact]
    public void Get_ReturnsOnlyThatAccountsFolders()
    {
        var one = AddFolder("X:\\One");
        var two = AddFolder("X:\\Two");
        _store.Create("Anna", [one]);
        var bob = _store.Create("Bob", [two]);

        Assert.Equal([two], _store.Get(bob.Id)!.FolderIds);
        Assert.Equal([two], _store.ByName("bob")!.FolderIds);
    }

    [Fact]
    public void Remove_RefusesOwner()
    {
        Assert.Throws<InvalidOperationException>(() => _store.Remove(LibraryDatabase.OwnerId));

        Assert.NotNull(_store.Get(LibraryDatabase.OwnerId));
    }

    [Fact]
    public void Remove_DeletesProgress_AndKeepsTheLogWithoutAUser()
    {
        var file = AddFile(AddFolder("X:\\Media"), "a.mkv");
        var account = _store.Create("Anna", null);
        new ProgressStore(_db).Save(account.Id, file, 10, 100);
        new ProgressStore(_db).Save(LibraryDatabase.OwnerId, file, 20, 100);
        var log = new SharingLog(_db);
        log.Add(SharingEvent.Joined, account.Id, "Anna joined");

        _store.Remove(account.Id);

        Assert.Null(_store.Get(account.Id));
        Assert.Null(new ProgressStore(_db).Get(account.Id, file));
        Assert.NotNull(new ProgressStore(_db).Get(LibraryDatabase.OwnerId, file));
        Assert.Null(Assert.Single(log.Recent(10)).UserId);
    }

    [Fact]
    public void SetFolders_NullMeansAll()
    {
        var folder = AddFolder("X:\\Media");
        var account = _store.Create("Anna", [folder]);

        _store.SetFolders(account.Id, null);
        Assert.Null(_store.Get(account.Id)!.FolderIds);

        _store.SetFolders(account.Id, [folder]);
        Assert.Equal([folder], _store.Get(account.Id)!.FolderIds);

        _store.SetFolders(account.Id, []);
        Assert.Empty(_store.Get(account.Id)!.FolderIds!);
    }

    [Fact]
    public void NewFolder_ReachesAllFoldersAccounts()
    {
        var first = AddFolder("X:\\One");
        var everything = _store.Create("Everything", null);
        var chosen = _store.Create("Chosen", [first]);

        var second = AddFolder("X:\\Two");

        Assert.True(_store.ViewerOf(_store.Get(everything.Id)!).CanReach(second));
        Assert.False(_store.ViewerOf(_store.Get(chosen.Id)!).CanReach(second));
        Assert.True(_store.ViewerOf(_store.Get(chosen.Id)!).CanReach(first));
    }

    [Fact]
    public void RemovedFolder_LeavesUserFolders()
    {
        var one = AddFolder("X:\\One");
        var two = AddFolder("X:\\Two");
        var account = _store.Create("Anna", [one, two]);

        using (var connection = _db.Open()) connection.Execute("DELETE FROM folders WHERE id = @one", new { one });

        Assert.Equal([two], _store.Get(account.Id)!.FolderIds);
    }

    [Fact]
    public void PasswordHash_RoundTrips_AndHasPasswordFollows()
    {
        var account = _store.Create("Anna", null);
        Assert.Null(_store.PasswordHash(account.Id));

        _store.SetPasswordHash(account.Id, "stored");
        Assert.Equal("stored", _store.PasswordHash(account.Id));
        Assert.True(_store.Get(account.Id)!.HasPassword);

        _store.SetPasswordHash(account.Id, null);
        Assert.False(_store.Get(account.Id)!.HasPassword);
    }

    [Fact]
    public void SetDisabled_AndTouch_AreStored()
    {
        var clock = new Support.FakeClock();
        var store = new AccountStore(_db, clock);
        var account = store.Create("Anna", null);

        store.SetDisabled(account.Id, true);
        store.Touch(account.Id);

        var read = store.Get(account.Id)!;
        Assert.True(read.Disabled);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, read.LastSeen);
    }

    [Fact]
    public void ViewerOf_CarriesTheAccountsFolders()
    {
        var folder = AddFolder("X:\\One");
        var viewer = _store.ViewerOf(_store.Create("Anna", [folder]));

        Assert.False(viewer.IsOwner);
        Assert.Equal(new HashSet<int> { folder }, viewer.AllowedFolderIds);
        Assert.Null(_store.ViewerOf(_store.Get(LibraryDatabase.OwnerId)!).AllowedFolderIds);
        Assert.True(_store.ViewerOf(_store.Get(LibraryDatabase.OwnerId)!).IsOwner);
    }
}
