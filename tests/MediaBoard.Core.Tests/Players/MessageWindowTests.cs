using MediaBoard.Core.Players.MpcHc;

namespace MediaBoard.Core.Tests.Players;

/// <summary>Real WM_COPYDATA between two message windows in this process; no MPC-HC needed.</summary>
public sealed class MessageWindowTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly MessageWindow _sender = new();
    private readonly MessageWindow _receiver = new();

    public void Dispose()
    {
        _sender.Dispose();
        _receiver.Dispose();
    }

    private async Task<(uint Cmd, string Payload)> RoundTrip(uint cmd, string payload)
    {
        var received = new TaskCompletionSource<(uint, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiver.CopyDataReceived += (c, p) => received.TrySetResult((c, p));

        _sender.Send(_receiver.Handle, cmd, payload);

        return await received.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(MpcApi.CMD_NOWPLAYING, @"A\|B’Ц\x|")]
    [InlineData(MpcApi.CMD_GETCURRENTPOSITION, "")]
    [InlineData(MpcApi.CMD_STATE, "2")]
    public async Task Send_ArrivesUnchanged(uint cmd, string payload)
    {
        Assert.Equal((cmd, payload), await RoundTrip(cmd, payload));
    }

    [Fact]
    public async Task Send_LongPayload_ArrivesUnchanged()
    {
        var payload = string.Concat(Enumerable.Repeat(@"G:\Torrent Downloads\Серіал ’S01’ [x]|", 900))[..32_000];

        Assert.Equal((MpcApi.CMD_NOWPLAYING, payload), await RoundTrip(MpcApi.CMD_NOWPLAYING, payload));
    }

    [Fact]
    public async Task Received_ComesOnTheWindowThread()
    {
        var thread = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        _receiver.CopyDataReceived += (_, _) => thread.TrySetResult(Thread.CurrentThread);

        _sender.Send(_receiver.Handle, MpcApi.CMD_STATE, "1");

        var handlerThread = await thread.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(ApartmentState.STA, handlerThread.GetApartmentState());
        Assert.NotEqual(Environment.CurrentManagedThreadId, handlerThread.ManagedThreadId);
    }
}
