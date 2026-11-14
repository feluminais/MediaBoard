using CozyCouch.Core.Settings;

namespace CozyCouch.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cozycouch-test-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SettingsStore_WritesThePosterSizeByName()
    {
        var store = new SettingsStore(SettingsPath);

        store.Save(new AppSettings { PosterSize = PosterSize.Large, UiSizePercent = 115 });

        Assert.Contains("\"posterSize\": \"Large\"", File.ReadAllText(SettingsPath));
        Assert.Equal(PosterSize.Large, new SettingsStore(SettingsPath).Load().PosterSize);
    }

    [Fact]
    public void SettingsStore_MissingFile_ReturnsDefaults()
    {
        var loaded = new SettingsStore(SettingsPath).Load();

        Assert.Equal(new AppSettings(), loaded);
    }

    [Fact]
    public void SettingsStore_CorruptFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ not json");

        Assert.Equal(new AppSettings(), new SettingsStore(SettingsPath).Load());
    }

    [Fact]
    public void Update_ChangesOneSetting_AndKeepsTheRest()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { TmdbKey = "k-123", DefaultPlayerId = "vlc" });

        store.Update(s => s with { DefaultPlayerId = "mpc-hc" });

        Assert.Equal(new AppSettings { TmdbKey = "k-123", DefaultPlayerId = "mpc-hc" }, store.Load());
    }

    [Fact]
    public void Update_WhenTheFileCannotBeRead_RefusesToSave_AndKeepsTheKey()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { TmdbKey = "k-123", DefaultPlayerId = "vlc" });

        // Another program holds the file without letting anyone read it (it may still be replaced).
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            Assert.Throws<SettingsUnreadableException>(() => store.Update(s => s with { DefaultPlayerId = "mpc-hc" }));
        }

        Assert.Equal(new AppSettings { TmdbKey = "k-123", DefaultPlayerId = "vlc" }, store.Load());
    }

    [Fact]
    public void Update_OverACorruptFile_StartsFromDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ not json");
        var store = new SettingsStore(SettingsPath);

        store.Update(s => s with { TmdbKey = "k-123" });

        Assert.Equal("k-123", store.Load().TmdbKey);
    }

    [Fact]
    public void TryLoad_NoFile_IsMissing()
    {
        Assert.IsType<SettingsReadResult.Missing>(new SettingsStore(SettingsPath).TryLoad());
    }

    [Fact]
    public void TryLoad_SavedFile_IsOk()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Mode = AppMode.ThisPc });

        var read = Assert.IsType<SettingsReadResult.Ok>(store.TryLoad());

        Assert.Equal(AppMode.ThisPc, read.Settings.Mode);
    }

    [Fact]
    public void TryLoad_FileHeldByAnotherProgram_IsUnreadable()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings());

        using var held = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Delete);

        Assert.IsType<SettingsReadResult.Unreadable>(store.TryLoad());
    }

    // Load and Update already treat a corrupt file as defaults, and a person can do nothing else with one.
    [Fact]
    public void TryLoad_CorruptFile_CountsAsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ not json");

        var read = Assert.IsType<SettingsReadResult.Ok>(new SettingsStore(SettingsPath).TryLoad());

        Assert.Equal(new AppSettings(), read.Settings);
    }

    [Fact]
    public void SettingsStore_WritesTheModeByName()
    {
        var store = new SettingsStore(SettingsPath);

        store.Save(new AppSettings { Mode = AppMode.Server });

        Assert.Contains("\"mode\": \"Server\"", File.ReadAllText(SettingsPath));
        Assert.Equal(AppMode.Server, new SettingsStore(SettingsPath).Load().Mode);
    }

    [Fact]
    public void SettingsStore_FileWithNoMode_IsUnset()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "tmdbKey": "k-123" }""");

        Assert.Equal(AppMode.Unset, new SettingsStore(SettingsPath).Load().Mode);
    }

    [Fact]
    public void SettingsStore_Save_RaisesChanged()
    {
        var store = new SettingsStore(SettingsPath);
        AppSettings? seen = null;
        store.Changed += s => seen = s;

        var saved = new AppSettings { TmdbKey = "abc" };
        store.Save(saved);

        Assert.Equal(saved, seen);
    }

    [Fact]
    public void SettingsStore_Save_CreatesMissingDirectory()
    {
        var nested = Path.Combine(_dir, "a", "b", "settings.json");

        new SettingsStore(nested).Save(new AppSettings { DefaultPlayerId = "vlc" });

        Assert.Equal("vlc", new SettingsStore(nested).Load().DefaultPlayerId);
    }
}
