using CozyCouch.Core.Remote;
using CozyCouch.Core.Settings;

namespace CozyCouch.Desktop.Tests;

public sealed class ModeGateTests
{
    public enum Stored
    {
        NoFile,
        HeldByAnotherProgram,
        Unset,
        ThisPc,
        ServerNoConnection,
        ServerConnected,
        ServerSignedOut,
        ServerBadFingerprint,
        ServerShortFingerprint,
        ServerNoAddresses,
    }

    private static SettingsReadResult Read(Stored stored)
    {
        var endpoint = new ServerEndpoint("Anna's library", ["192.168.1.5:47880"], new string('A', 64));
        return stored switch
        {
            Stored.NoFile => new SettingsReadResult.Missing(),
            Stored.HeldByAnotherProgram => new SettingsReadResult.Unreadable(),
            Stored.Unset => Ok(new AppSettings()),
            Stored.ThisPc => Ok(new AppSettings { Mode = AppMode.ThisPc }),
            Stored.ServerNoConnection => Ok(new AppSettings { Mode = AppMode.Server }),
            Stored.ServerConnected => Server(endpoint, "blob"),
            Stored.ServerSignedOut => Server(endpoint, null),
            Stored.ServerBadFingerprint => Server(endpoint with { Fingerprint = new string('Z', 64) }, "blob"),
            Stored.ServerShortFingerprint => Server(endpoint with { Fingerprint = "ABCD" }, "blob"),
            Stored.ServerNoAddresses => Server(endpoint with { Addresses = [] }, "blob"),
            _ => throw new ArgumentOutOfRangeException(nameof(stored)),
        };

        static SettingsReadResult Ok(AppSettings settings) => new SettingsReadResult.Ok(settings);

        static SettingsReadResult Server(ServerEndpoint endpoint, string? token) =>
            Ok(new AppSettings { Mode = AppMode.Server, Connection = new StoredConnection(endpoint, "Sam", "Laptop", token) });
    }

    [Theory]
    [InlineData(Stored.Unset, true, StartupChoice.RunLocal)]
    [InlineData(Stored.Unset, false, StartupChoice.AskFirstRun)]
    [InlineData(Stored.NoFile, true, StartupChoice.RunLocal)]
    [InlineData(Stored.NoFile, false, StartupChoice.AskFirstRun)]
    [InlineData(Stored.ThisPc, false, StartupChoice.RunLocal)]
    [InlineData(Stored.ThisPc, true, StartupChoice.RunLocal)]
    [InlineData(Stored.ServerConnected, false, StartupChoice.RunRemote)]
    [InlineData(Stored.ServerConnected, true, StartupChoice.RunRemote)]
    [InlineData(Stored.ServerNoConnection, false, StartupChoice.AskFirstRun)]
    [InlineData(Stored.ServerNoConnection, true, StartupChoice.AskFirstRun)]
    [InlineData(Stored.HeldByAnotherProgram, false, StartupChoice.ShowSettingsError)]
    [InlineData(Stored.HeldByAnotherProgram, true, StartupChoice.ShowSettingsError)]

    // The extra case: the server is known, but this device has nothing to connect with.
    [InlineData(Stored.ServerSignedOut, false, StartupChoice.ShowConnect)]
    [InlineData(Stored.ServerBadFingerprint, false, StartupChoice.ShowConnect)]
    [InlineData(Stored.ServerShortFingerprint, false, StartupChoice.ShowConnect)]
    [InlineData(Stored.ServerNoAddresses, false, StartupChoice.ShowConnect)]
    public void Decide(Stored stored, bool databaseExists, StartupChoice expected)
    {
        Assert.Equal(expected, ModeGate.Decide(Read(stored), databaseExists));
    }

    // Hand-edited JSON can leave out what the record's types promise.
    [Fact]
    public void Decide_ConnectionWithNoEndpoint_ShowsConnect()
    {
        var settings = new AppSettings { Mode = AppMode.Server, Connection = new StoredConnection(null!, "Sam", "Laptop", "blob") };

        Assert.Equal(StartupChoice.ShowConnect, ModeGate.Decide(new SettingsReadResult.Ok(settings), databaseExists: false));
    }
}
