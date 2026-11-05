using MediaBoard.Core.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace MediaBoard.Server;

/// <summary>
/// Sends a file with range support, and cuts the connection the moment <c>stop</c> fires or the file goes wrong.
/// Headers are out by then, so a cut is all that can be said: the client finds out why from its next request, which
/// is answered with a code.
/// </summary>
internal sealed class FileStreamResult(FileStream file, string path, StreamRegistry streams, StreamLease lease, CancellationToken stop) : IResult
{
    private const int BufferBytes = 1024 * 1024;

    /// <summary>
    /// Opens the file the way a seeding torrent client needs: it can go on writing, move the file or delete it.
    /// </summary>
    /// <exception cref="IOException">The file can't be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The file can't be opened.</exception>
    public static FileStream OpenFile(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferBytes,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>What to tell a client about a file that can't be opened.</summary>
    public static ApiErrorCode ProblemWith(string path) =>
        Path.GetPathRoot(path) is { Length: > 0 } root && !Directory.Exists(root) ? ApiErrorCode.DriveUnavailable : ApiErrorCode.FileMissing;

    public async Task ExecuteAsync(HttpContext context)
    {
        using (lease)
        await using (var body = new Body(file, path, streams, lease.Session, stop))
        await using (stop.Register(context.Abort))
        {
            // A paused player stops reading, and that is no reason to drop it. The session's quiet limit ends a dead one.
            if (context.Features.Get<IHttpMinResponseDataRateFeature>() is { } rate) rate.MinDataRate = null;

            await Results.Stream(body, enableRangeProcessing: true).ExecuteAsync(context);
        }
    }

    /// <summary>
    /// The file as it was when opened. Only what the framework's range copy calls is implemented. A failure comes out
    /// as cancellation, which that copy answers by aborting the connection: any other exception would land in the log
    /// with the file's path in its message.
    /// </summary>
    private sealed class Body(FileStream file, string path, StreamRegistry streams, Guid session, CancellationToken stop) : Stream
    {
        // Pinned, because a torrent client may be appending: sending more than the headers promised breaks the response.
        private readonly long _length = file.Length;
        private long _position;
        private long _sinceNameCheck;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var left = _length - _position;
            if (left <= 0) return 0;

            var read = await ReadFileAsync(buffer[..(int)Math.Min(buffer.Length, left)]);
            _position += read;
            streams.AddBytes(session, read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = file.Seek(offset, origin);
            return _position;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }

        private async ValueTask<int> ReadFileAsync(Memory<byte> buffer)
        {
            try
            {
                // A deleted or moved file stays readable through this handle, so only its name can show it has gone.
                // Asked once per disk read, which costs nothing beside that read.
                if (_sinceNameCheck >= BufferBytes)
                {
                    _sinceNameCheck = 0;
                    if (!File.Exists(path)) throw new OperationCanceledException(stop);
                }

                var read = await file.ReadAsync(buffer, stop);
                _sinceNameCheck += read;

                // Short of what the headers promised: the file shrank.
                return read > 0 ? read : throw new OperationCanceledException(stop);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new OperationCanceledException(stop);
            }
        }
    }
}
