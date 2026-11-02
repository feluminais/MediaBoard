using Avalonia.Media;

namespace MediaBoard.Desktop.Controls;

/// <summary>The colour bands of the episode graph, best first. <see cref="Unrated"/> is the grey "?" cell.</summary>
public enum RatingBand { Absolute, Awesome, Great, Good, Average, Bad, Garbage, Unrated }

public static class RatingBands
{
    /// <summary>The legend, in the order it is shown.</summary>
    public static IReadOnlyList<RatingBand> Legend { get; } =
        [RatingBand.Absolute, RatingBand.Awesome, RatingBand.Great, RatingBand.Good, RatingBand.Average, RatingBand.Bad, RatingBand.Garbage];

    /// <summary>The band of a rating, taken as it is drawn (one decimal); null is unrated.</summary>
    public static RatingBand Of(double? rating) => rating switch
    {
        null => RatingBand.Unrated,
        >= 9.7 => RatingBand.Absolute,
        >= 9.0 => RatingBand.Awesome,
        >= 8.0 => RatingBand.Great,
        >= 7.0 => RatingBand.Good,
        >= 6.0 => RatingBand.Average,
        >= 4.0 => RatingBand.Bad,
        _ => RatingBand.Garbage,
    };

    public static string Name(RatingBand band) => band switch
    {
        RatingBand.Absolute => "Absolute Cinema",
        RatingBand.Awesome => "Awesome",
        RatingBand.Great => "Great",
        RatingBand.Good => "Good",
        RatingBand.Average => "Average",
        RatingBand.Bad => "Bad",
        RatingBand.Garbage => "Garbage",
        _ => "Not rated",
    };

    public static IBrush Fill(RatingBand band) => band switch
    {
        RatingBand.Absolute => AbsoluteFill,
        RatingBand.Awesome => AwesomeFill,
        RatingBand.Great => GreatFill,
        RatingBand.Good => GoodFill,
        RatingBand.Average => AverageFill,
        RatingBand.Bad => BadFill,
        RatingBand.Garbage => GarbageFill,
        _ => UnratedFill,
    };

    /// <summary>Dark text on the light bands, white on the dark ones.</summary>
    public static bool DarkText(RatingBand band) => band is RatingBand.Great or RatingBand.Good or RatingBand.Average or RatingBand.Unrated;

    private static readonly IBrush AbsoluteFill = new SolidColorBrush(Color.Parse("#1E96F0"));
    private static readonly IBrush AwesomeFill = new SolidColorBrush(Color.Parse("#17753D"));
    private static readonly IBrush GreatFill = new SolidColorBrush(Color.Parse("#2BB567"));
    private static readonly IBrush GoodFill = new SolidColorBrush(Color.Parse("#F4CF40"));
    private static readonly IBrush AverageFill = new SolidColorBrush(Color.Parse("#F29B1D"));
    private static readonly IBrush BadFill = new SolidColorBrush(Color.Parse("#E5483B"));
    private static readonly IBrush GarbageFill = new SolidColorBrush(Color.Parse("#6B3FA0"));
    private static readonly IBrush UnratedFill = new SolidColorBrush(Color.Parse("#C9C6C0"));
}
