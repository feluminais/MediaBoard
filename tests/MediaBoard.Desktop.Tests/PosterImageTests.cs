using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MediaBoard.Desktop.Controls;
using MediaBoard.Desktop.Tests.Support;

namespace MediaBoard.Desktop.Tests;

public class PosterImageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-poster-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Window Host(PosterImage image)
    {
        var window = new Window { Width = 300, Height = 400, Content = image };
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public void PosterImage_NullPath_ShowsPlaceholder()
    {
        var image = new PosterImage { Title = "Klaus", LocalPath = null };
        Host(image);

        Assert.True(image.ShowsPlaceholder);
        Assert.Equal("Klaus", image.PlaceholderText);
        Assert.Null(image.Image);
    }

    [AvaloniaFact]
    public async Task PosterImage_DecodesAtDisplayWidth()
    {
        var path = Posters.WritePng(_dir, "big.png", 400, 600);
        var image = new PosterImage { Title = "Klaus", DecodeWidth = 100 };
        Host(image);

        image.LocalPath = path;
        await Ui.WaitUntil(() => image.Image is not null);

        Assert.False(image.ShowsPlaceholder);
        Assert.Equal(100, image.Image!.PixelSize.Width); // not the file's 400
        Assert.Equal(150, image.Image.PixelSize.Height); // aspect ratio kept
    }

    [AvaloniaFact]
    public async Task PosterImage_UndecodableFile_ShowsPlaceholder()
    {
        var path = Path.Combine(_dir, "broken.jpg");
        await File.WriteAllTextAsync(path, "not an image");
        var image = new PosterImage { Title = "Klaus" };
        Host(image);

        image.LocalPath = path;
        await Ui.WaitUntil(() => !image.IsLoading);

        Assert.True(image.ShowsPlaceholder);
    }

    [AvaloniaFact]
    public async Task PosterImage_MissingFile_ShowsPlaceholder()
    {
        var image = new PosterImage { Title = "Klaus" };
        Host(image);

        image.LocalPath = Path.Combine(_dir, "gone.jpg");
        await Ui.WaitUntil(() => !image.IsLoading);

        Assert.True(image.ShowsPlaceholder);
    }

    [AvaloniaFact]
    public async Task PosterImage_SamePathAgain_KeepsTheBitmap()
    {
        var path = Posters.WritePng(_dir, "a.png", 200, 300);
        var image = new PosterImage { DecodeWidth = 100 };
        Host(image);
        image.LocalPath = path;
        await Ui.WaitUntil(() => image.Image is not null);
        var shown = image.Image;

        image.LocalPath = null;
        image.LocalPath = path; // a recycled card goes through null on its way back
        Assert.Same(shown, image.Image); // the cache has it: no placeholder frame in between
    }

    [AvaloniaFact]
    public async Task PosterImage_ClearedPath_ShowsPlaceholder()
    {
        var path = Posters.WritePng(_dir, "a.png", 200, 300);
        var image = new PosterImage { DecodeWidth = 100 };
        Host(image);
        image.LocalPath = path;
        await Ui.WaitUntil(() => image.Image is not null);

        image.LocalPath = null;

        Assert.True(image.ShowsPlaceholder);
    }

    [AvaloniaFact]
    public async Task PosterImage_NewPath_ShowsTheNewPicture()
    {
        var first = Posters.WritePng(_dir, "a.png", 200, 300);
        var second = Posters.WritePng(_dir, "b.png", 200, 300);
        var image = new PosterImage { DecodeWidth = 100 };
        Host(image);
        image.LocalPath = first;
        await Ui.WaitUntil(() => image.Image is not null);
        var firstImage = image.Image;

        image.LocalPath = second;
        await Ui.WaitUntil(() => image.Image is not null && !ReferenceEquals(image.Image, firstImage));

        Assert.NotSame(firstImage, image.Image);
    }

    [Fact]
    public void LeaseCache_EvictsTheOldest_AndDisposesIt()
    {
        var cache = new LeaseCache<Probe>(2);
        var a = cache.Add("a", new Probe());
        cache.Release(a);
        var b = cache.Add("b", new Probe());
        cache.Release(b);

        cache.Release(cache.Add("c", new Probe()));

        Assert.True(a.Value!.Disposed);
        Assert.False(b.Value!.Disposed);
        Assert.Null(cache.Acquire("a"));
    }

    [Fact]
    public void LeaseCache_DoesNotDisposeWhileInUse()
    {
        var cache = new LeaseCache<Probe>(1);
        var a = cache.Add("a", new Probe()); // still shown

        cache.Release(cache.Add("b", new Probe()));
        Assert.False(a.Value!.Disposed);

        cache.Release(a); // the control let go
        Assert.True(a.Value.Disposed);
    }

    [Fact]
    public void LeaseCache_ReadingKeepsAnEntryFresh()
    {
        var cache = new LeaseCache<Probe>(2);
        var a = cache.Add("a", new Probe());
        cache.Release(a);
        cache.Release(cache.Add("b", new Probe()));

        cache.Release(cache.Acquire("a")!); // a is now newer than b
        cache.Release(cache.Add("c", new Probe()));

        Assert.False(a.Value!.Disposed);
        Assert.Null(cache.Acquire("b"));
    }

    private sealed class Probe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
