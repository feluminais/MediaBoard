using Avalonia.Controls;
using CozyCouch.Core.Settings;
using CozyCouch.Desktop.Sizing;

namespace CozyCouch.Desktop.Tests;

public class UiSizesTests
{
    [Theory]
    [InlineData(1599, 0)]
    [InlineData(1600, 1)]
    [InlineData(2560, 2)]
    [InlineData(3840, 3)]
    public void WiderWindows_StepUp(double width, int step) => Assert.Equal(step, UiSizes.StepFor(width, growWithWindow: true));

    [Fact]
    public void GrowingOff_KeepsTheSizesAtEveryWidth() =>
        Assert.Equal(UiSizes.For(new AppSettings { GrowWithWindow = false }, 1200), UiSizes.For(new AppSettings { GrowWithWindow = false }, 3840));

    [Fact]
    public void UiSize_ScalesTextOnly_AndPosterSize_PostersOnly()
    {
        var baseline = UiSizes.For(new AppSettings(), 1200);
        var biggerText = UiSizes.For(new AppSettings { UiSizePercent = 130 }, 1200);
        var smallerPosters = UiSizes.For(new AppSettings { PosterSize = PosterSize.Small }, 1200);

        Assert.True(biggerText.TextScale > baseline.TextScale);
        Assert.Equal(baseline.PosterWidth, biggerText.PosterWidth);
        Assert.True(smallerPosters.PosterWidth < baseline.PosterWidth);
        Assert.Equal(baseline.TextScale, smallerPosters.TextScale);
    }

    [Fact]
    public void Posters_AreDecodedAtTheirPixelWidth()
    {
        var resources = new ResourceDictionary();

        UiSizes.For(new AppSettings { PosterSize = PosterSize.Large }, 2560).Apply(resources, renderScaling: 1.5);

        // 260 px wide (160 x 1.25 x 1.3) at 150% is 390 pixels, rounded up to 400.
        Assert.Equal(400, resources["PosterDecodeWidth"]);
    }
}
