using MediaBoard.Desktop.Tests.Support;

namespace MediaBoard.Desktop.Tests;

public class PlayerExitPromptTests
{
    private readonly FakePlayback _playback = new();
    private readonly List<string> _asked = [];

    private PlayerExitPrompt Prompt(bool closeIt) => new(_playback, player =>
    {
        _asked.Add(player);
        lock (_playback.Calls) _playback.Calls.Add("ask");
        return Task.FromResult(closeIt);
    });

    [Fact]
    public async Task Playing_SavesFirst_ThenClosesThePlayer_WhenAskedTo()
    {
        _playback.CurrentPlayer = "MPC-HC";

        await Prompt(closeIt: true).RunAsync();

        Assert.Equal(["MPC-HC"], _asked);
        Assert.Equal(["save", "ask", "stop:close"], _playback.Calls);
    }

    [Fact]
    public async Task Playing_LeavesThePlayerRunning_WhenAskedTo()
    {
        _playback.CurrentPlayer = "VLC";

        await Prompt(closeIt: false).RunAsync();

        Assert.Equal(["VLC"], _asked);
        Assert.Equal(["save", "ask", "stop:leave"], _playback.Calls);
    }

    [Fact]
    public async Task NothingPlaying_AsksNothing()
    {
        await Prompt(closeIt: true).RunAsync();

        Assert.Empty(_asked);
        Assert.Empty(_playback.Calls);
    }
}
