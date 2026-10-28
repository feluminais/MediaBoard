using MediaBoard.Core.Settings;

namespace MediaBoard.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediaboard-test-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SettingsStore_RoundTrips()
    {
        var store = new SettingsStore(SettingsPath);
        var saved = new AppSettings { TmdbKey = "k-123", DefaultPlayerId = "mpc-hc", CustomPlayerPath = "C:\\Tools\\player.exe" };

        store.Save(saved);
        var loaded = new SettingsStore(SettingsPath).Load();

        Assert.Equal(saved, loaded);
    }

    [Fact]
    public void SettingsStore_MissingFile_ReturnsDefaults()
    {
        var loaded = new SettingsStore(SettingsPath).Load();

        Assert.Equal(new AppSettings(), loaded);
        Assert.Null(loaded.TmdbKey);
    }

    [Fact]
    public void SettingsStore_CorruptFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ not json");

        Assert.Equal(new AppSettings(), new SettingsStore(SettingsPath).Load());
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
