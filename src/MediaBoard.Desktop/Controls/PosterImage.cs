using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MediaBoard.Desktop.Controls;

/// <summary>
/// A poster picture. It decodes the file on a background thread at <see cref="DecodeWidth"/> (never at the file's full
/// size), shares decoded bitmaps app-wide through a cache of <see cref="CacheCapacity"/>, and shows the title on a plain
/// block while there is no picture: no path, a file that can't be read, or a decode still running. Setting the path
/// the control already shows changes nothing, so a refresh doesn't flicker.
/// </summary>
public sealed class PosterImage : Control
{
    /// <summary>How many decoded posters the app keeps in memory.</summary>
    public const int CacheCapacity = 300;

    public static readonly StyledProperty<string?> LocalPathProperty =
        AvaloniaProperty.Register<PosterImage, string?>(nameof(LocalPath));

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<PosterImage, int>(nameof(DecodeWidth), 240);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PosterImage, string?>(nameof(Title));

    private static readonly LeaseCache<Bitmap> Shared = new(CacheCapacity);
    private static readonly Dictionary<string, Task<Bitmap?>> Decoding = []; // UI thread only

    private LeaseCache<Bitmap>.Entry? _lease;
    private string? _key;
    private int _generation;
    private bool _attached;
    private IBrush? _blockBrush;
    private IBrush? _textBrush;
    private FormattedText? _text;
    private Size _textSize;

    static PosterImage()
    {
        AffectsRender<PosterImage>(TitleProperty);
        LocalPathProperty.Changed.AddClassHandler<PosterImage>((image, _) => image.Reload());
        DecodeWidthProperty.Changed.AddClassHandler<PosterImage>((image, _) => image.Reload());
        TitleProperty.Changed.AddClassHandler<PosterImage>((image, _) => image._text = null);
    }

    public PosterImage() => ActualThemeVariantChanged += OnThemeChanged;

    /// <summary>The cached image file to show, or null for the placeholder.</summary>
    public string? LocalPath
    {
        get => GetValue(LocalPathProperty);
        set => SetValue(LocalPathProperty, value);
    }

    /// <summary>The width, in pixels, the picture is decoded at: the width it is drawn at.</summary>
    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    /// <summary>What the placeholder says.</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The picture being shown, or null while the placeholder is.</summary>
    public Bitmap? Image => _lease?.Value;

    public bool ShowsPlaceholder => Image is null;

    public string? PlaceholderText => Title;

    /// <summary>A decode is running for the current path.</summary>
    public bool IsLoading { get; private set; }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        if (Image is { } bitmap && bitmap.Size.Width > 0 && bitmap.Size.Height > 0)
        {
            // Fill the box, crop what overflows.
            var scale = Math.Max(bounds.Width / bitmap.Size.Width, bounds.Height / bitmap.Size.Height);
            var size = new Size(bitmap.Size.Width * scale, bitmap.Size.Height * scale);
            var target = new Rect((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2, size.Width, size.Height);
            using (context.PushClip(bounds)) context.DrawImage(bitmap, new Rect(bitmap.Size), target);
            return;
        }

        context.DrawRectangle(_blockBrush ?? Brushes.Gray, null, bounds);
        if (string.IsNullOrWhiteSpace(Title)) return;

        const double margin = 10;
        var available = new Size(Math.Max(0, bounds.Width - 2 * margin), Math.Max(0, bounds.Height - 2 * margin));
        if (_text is null || _textSize != available)
        {
            _text = new FormattedText(Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 14, _textBrush ?? Brushes.White)
            {
                MaxTextWidth = available.Width,
                MaxTextHeight = available.Height,
                TextAlignment = TextAlignment.Center,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            _textSize = available;
        }

        context.DrawText(_text, new Point(margin, Math.Max(margin, (bounds.Height - _text.Height) / 2)));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ReadBrushes();
        Reload();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Reload(); // lets go of the bitmap, which the cache may then dispose
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ReadBrushes();
        _text = null;
        InvalidateVisual();
    }

    private void ReadBrushes()
    {
        _blockBrush = FindBrush("PosterPlaceholderBrush");
        _textBrush = FindBrush("MutedTextBrush");
    }

    private IBrush? FindBrush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;

    // Shows what the current path and width call for, with as little change on screen as possible.
    private void Reload()
    {
        var path = LocalPath;
        var width = DecodeWidth;
        if (!_attached || string.IsNullOrEmpty(path) || width <= 0)
        {
            _generation++;
            IsLoading = false;
            Show(null, null);
            return;
        }

        var key = $"{width}|{path}";
        if (key == _key) return; // the same picture again: keep what is there

        var generation = ++_generation;
        var cached = Shared.Acquire(key);
        if (cached is not null)
        {
            IsLoading = false;
            Show(cached, key);
            return;
        }

        // A different picture that isn't ready: show the placeholder, not the previous title's poster.
        IsLoading = true;
        Show(null, key);
        _ = LoadAsync(key, path, width, generation);
    }

    private async Task LoadAsync(string key, string path, int width, int generation)
    {
        if (!Decoding.TryGetValue(key, out var decode))
        {
            Decoding[key] = decode = Task.Run(() => Decode(path, width));
        }

        var bitmap = await decode; // resumes on the UI thread
        if (Decoding.TryGetValue(key, out var current) && ReferenceEquals(current, decode)) Decoding.Remove(key);

        // The first control back stores the bitmap; the others find it there.
        var entry = Shared.Acquire(key) ?? Shared.Add(key, bitmap);
        if (generation != _generation || !_attached)
        {
            Shared.Release(entry); // the control moved on while this decoded
            return;
        }

        IsLoading = false;
        Show(entry, key);
    }

    private void Show(LeaseCache<Bitmap>.Entry? entry, string? key)
    {
        var old = _lease;
        _lease = entry;
        _key = key;
        if (old is not null) Shared.Release(old);
        InvalidateVisual();
    }

    private static Bitmap? Decode(string path, int width)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception)
        {
            return null; // missing, locked or not an image: the placeholder stays
        }
    }
}
