using System.ComponentModel;

namespace CozyCouch.Desktop.Tests;

public sealed class AppRestartTests
{
    private readonly List<string> _steps = [];

    private AppRestart Restart(string? path, Exception? refusal = null) => new(
        path,
        (file, arguments) =>
        {
            if (refusal is not null) throw refusal;
            _steps.Add($"start {file} {arguments}");
        },
        () => _steps.Add("shut down"));

    // The new process is started first, so a start that fails leaves the app running. It waits for this one on the flag.
    [Fact]
    public void Run_StartsTheNewProcess_ThenShutsDown()
    {
        Assert.True(Restart(@"C:\CozyCouch.exe").Run());

        Assert.Equal([$@"start C:\CozyCouch.exe {SingleInstance.RestartArgument}", "shut down"], _steps);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Run_NoProcessPath_KeepsRunning(string? path)
    {
        Assert.False(Restart(path).Run());

        Assert.Empty(_steps);
    }

    [Fact]
    public void Run_StartRefused_KeepsRunning()
    {
        Assert.False(Restart(@"C:\CozyCouch.exe", new Win32Exception("Access is denied.")).Run());

        Assert.Empty(_steps);
    }
}
