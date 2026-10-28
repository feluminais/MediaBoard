using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MediaBoard.Core.Playback;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Tests.Support;

/// <summary>Records what the UI asked it to play, and on which thread.</summary>
internal sealed class FakePlayback : IPlaybackService
{
    private readonly List<(int FileId, bool FromStart, int ThreadId)> _played = [];

    public IReadOnlyList<(int FileId, bool FromStart, int ThreadId)> Played
    {
        get
        {
            lock (_played) return _played.ToList();
        }
    }

    public event Action<string>? AskFinished
    {
        add { }
        remove { }
    }

    public Task PlayAsync(int fileId, bool fromStart, CancellationToken ct)
    {
        lock (_played) _played.Add((fileId, fromStart, Environment.CurrentManagedThreadId));
        return Task.CompletedTask;
    }

    public void AnswerFinished(int fileId, bool finished)
    {
    }
}

internal static class Posters
{
    /// <summary>A loader for a machine with no posters on disk and no network.</summary>
    public static PosterLoader None() => new((_, _, _) => Task.FromResult<string?>(null));

    /// <summary>A loader that answers <c>local:{path}</c> for every poster and records what it was asked for.</summary>
    public static PosterLoader Local(List<string> asked) => new((path, _, _) =>
    {
        lock (asked) asked.Add(path);
        return Task.FromResult<string?>($"local:{path}");
    });

    /// <summary>Writes a plain PNG of the given size and returns its path.</summary>
    public static string WritePng(string directory, string name, int width, int height)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
        {
            var pixels = new byte[frame.RowBytes * height];
            for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 80; pixels[i + 2] = 40; pixels[i + 3] = 255; }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
        }

        bitmap.Save(path, new PngBitmapEncoderOptions());
        return path;
    }
}
