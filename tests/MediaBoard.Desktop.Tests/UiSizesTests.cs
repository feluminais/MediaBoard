using Avalonia;
using Avalonia.Controls;
using MediaBoard.Core.Settings;
using MediaBoard.Desktop.Sizing;

namespace MediaBoard.Desktop.Tests;

public class UiSizesTests
{
    [Theory]
    [InlineData(1200, 0)]
    [InlineData(1599, 0)]
    [InlineData(1600, 1)]
    [InlineData(2560, 2)]
    [InlineData(3840, 3)]
    public void WiderWindows_StepUp(double width, int step) => Assert.Equal(step, UiSizes.StepFor(width, growWithWindow: true));

    [Fact]
    public void GrowingOff_KeepsTheSizesAtEveryWidth() =>
        Assert.Equal(UiSizes.For(new AppSettings { GrowWithWindow = false }, 1200), UiSizes.For(new AppSettings { GrowWithWindow = false }, 3840));

    [Fact]
    public void Defaults_MatchTheSizesInAppAxaml()
    {
        var resources = new ResourceDictionary();

        UiSizes.For(new AppSettings(), 1200).Apply(resources, renderScaling: 1.5);

        Assert.Equal(14.0, resources["BodyFontSize"]);
        Assert.Equal(28.0, resources["PageTitleFontSize"]);
        Assert.Equal(160.0, resources["PosterWidth"]);
        Assert.Equal(240.0, resources["PosterHeight"]);
        Assert.Equal(240, resources["PosterDecodeWidth"]);
        Assert.Equal(176.0, resources["PosterCellWidth"]);
        Assert.Equal(304.0, resources["PosterCellHeight"]);
        Assert.Equal(360, resources["TitlePosterDecodeWidth"]);
        Assert.Equal(220.0, resources["SidebarWidth"]);
        Assert.Equal(36.0, resources["NavItemHeight"]);
        Assert.Equal(640.0, resources["ReadingWidth"]);
        Assert.Equal(28.0, resources["SearchPillHeight"]);
        Assert.Equal(new Thickness(32, 12, 32, 0), resources["PageMargin"]);
    }

    [Fact]
    public void AWideWindow_MakesPostersGrowMoreThanText()
    {
        var sizes = UiSizes.For(new AppSettings(), 2560);

        Assert.Equal(208, sizes.PosterWidth); // 160 x 1.3
        Assert.Equal(1.24, sizes.TextScale, 3);
    }

    [Fact]
    public void UiSize_ScalesTextOnly_AndPosterSize_PostersOnly()
    {
        var larger = UiSizes.For(new AppSettings { UiSizePercent = 130, PosterSize = PosterSize.Small }, 1200);

        Assert.Equal(1.3, larger.TextScale, 3);
        Assert.Equal(128, larger.PosterWidth); // 160 x 0.8
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
