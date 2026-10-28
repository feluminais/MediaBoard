using System.IO.Pipes;
using System.Text;

namespace MediaBoard.Core.Players.Mpv;

/// <summary>A connection to mpv's IPC pipe: lines in, lines out.</summary>
public interface IMpvPipe : IDisposable
{
    /// <summary>The next line mpv sent, or null when it closed the pipe.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);

    Task WriteLineAsync(string line, CancellationToken ct);
}

/// <summary>An <see cref="IMpvPipe"/> over a Windows named pipe.</summary>
internal sealed class NamedPipeMpvPipe : IMpvPipe
{
    // mpv creates the pipe a moment after it starts; each attempt waits this long for it to appear.
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(200);

    private readonly NamedPipeClientStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private NamedPipeMpvPipe(NamedPipeClientStream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    /// <summary>One attempt to connect to <c>\\.\pipe\&lt;pipeName&gt;</c>; null when it is not there yet.</summary>
    public static async Task<IMpvPipe?> TryConnectAsync(string pipeName, CancellationToken ct)
    {
        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync((int)AttemptTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            return new NamedPipeMpvPipe(stream);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            stream.Dispose();
            return null;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async Task<string?> ReadLineAsync(CancellationToken ct) =>
        await _reader.ReadLineAsync(ct).ConfigureAwait(false);

    public Task WriteLineAsync(string line, CancellationToken ct) =>
        _writer.WriteLineAsync(line.AsMemory(), ct);

    public void Dispose() => _stream.Dispose();
}
