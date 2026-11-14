using Avalonia;
using Avalonia.Controls;
using CozyCouch.Core.Settings;

namespace CozyCouch.Desktop.Sizing;

/// <summary>
/// The sizes the views are drawn at: one record worked out from the Display settings and the main window's width, then
/// written into the app's resources (<see cref="Apply"/>) under the keys the views read with DynamicResource. App.axaml
/// holds the same keys at their default values, for the designer and the headless tests.
/// </summary>
public sealed record UiSizes(double TextScale, double PosterWidth, double PosterGap, double SidebarWidth, double PageGutter, double TitlePosterWidth)
{
    /// <summary>Window widths, in device-independent pixels, at which the layout steps up once more.</summary>
    public static readonly IReadOnlyList<double> StepWidths = [1600, 2200, 3000];

    /// <summary>The UI size choices on the Settings page, in percent.</summary>
    public static readonly IReadOnlyList<int> UiSizeChoices = [90, 100, 115, 130, 150];

    // Each step makes text 12% and posters 15% larger: posters carry a wide window, text only has to stay readable
    // from further away.
    private const double TextPerStep = 0.12;
    private const double PosterPerStep = 0.15;

    // Font sizes at 100%, by resource key.
    private static readonly (string Key, double Size)[] Fonts =
    [
        ("CaptionFontSize", 11),
        ("SmallFontSize", 12),
        ("MetaFontSize", 13),
        ("BodyFontSize", 14),
        ("ControlContentThemeFontSize", 14), // Fluent's own key for text in buttons, boxes and lists
        ("RowTitleFontSize", 17),
        ("DialogTitleFontSize", 18),
        ("EmptyTitleFontSize", 20),
        ("PageTitleFontSize", 28),
    ];

    public double PosterHeight => PosterWidth * 1.5;

    public double TitlePosterHeight => TitlePosterWidth * 1.5;

    /// <summary>How many steps a window of <paramref name="width"/> takes; 0 when growing is off.</summary>
    public static int StepFor(double width, bool growWithWindow) =>
        growWithWindow ? StepWidths.Count(w => width >= w) : 0;

    public static UiSizes For(AppSettings settings, double windowWidth)
    {
        var step = StepFor(windowWidth, settings.GrowWithWindow);
        var text = Math.Clamp(settings.UiSizePercent, 50, 300) / 100.0 * (1 + TextPerStep * step);
        var poster = (1 + PosterPerStep * step) * settings.PosterSize switch
        {
            PosterSize.Small => 0.8,
            PosterSize.Large => 1.25,
            _ => 1.0,
        };

        return new UiSizes(
            TextScale: text,
            PosterWidth: Even(160 * poster),
            PosterGap: Math.Round(16 * (1 + PosterPerStep * step)),
            SidebarWidth: Math.Round(220 * text),
            PageGutter: 32 + 8 * step,
            TitlePosterWidth: Even(220 * (1 + PosterPerStep * step)));
    }

    /// <summary>
    /// Writes every size into <paramref name="resources"/>. <paramref name="renderScaling"/> is the window's display
    /// scaling: posters are decoded at the pixel width they are drawn at, never narrower.
    /// </summary>
    public void Apply(IResourceDictionary resources, double renderScaling)
    {
        foreach (var (key, size) in Fonts) resources[key] = Math.Round(size * TextScale * 2) / 2;

        resources["PosterWidth"] = PosterWidth;
        resources["PosterHeight"] = PosterHeight;
        resources["PosterDecodeWidth"] = DecodeWidth(PosterWidth, renderScaling);
        resources["PosterGap"] = PosterGap;
        resources["PosterCardMargin"] = new Thickness(0, 0, PosterGap, 12);
        resources["PosterCellWidth"] = PosterWidth + PosterGap;
        // The poster, then the title and subtitle lines under it and the 12 px under the card.
        resources["PosterCellHeight"] = Math.Ceiling(PosterHeight + 64 * TextScale);
        resources["TitlePosterWidth"] = TitlePosterWidth;
        resources["TitlePosterHeight"] = TitlePosterHeight;
        resources["TitlePosterDecodeWidth"] = DecodeWidth(TitlePosterWidth, renderScaling);
        resources["SidebarWidth"] = SidebarWidth;
        resources["NavItemHeight"] = Math.Round(36 * TextScale);
        // The widest a column of settings or prose gets, so lines stay the same length in characters as text grows.
        resources["ReadingWidth"] = Math.Round(640 * TextScale);
        // The title strip stays 40 px (the window buttons are drawn for it), so the search pill stops short of it.
        resources["SearchPillHeight"] = Math.Min(34, Math.Round(28 * TextScale));
        resources["PageMargin"] = new Thickness(PageGutter, 12, PageGutter, 0);
    }

    private static double Even(double value) => Math.Round(value / 2) * 2;

    // Rounded up to 40 px, so a small change in size reuses the decoded pictures already in the cache.
    private static int DecodeWidth(double width, double renderScaling) =>
        (int)(Math.Ceiling(width * Math.Max(1, renderScaling) / 40) * 40);
}
