using MediaBoard.Core.Parsing;

namespace MediaBoard.Core.Tests.Parsing;

public class MediaFileRulesTests
{
    private const long MB = 1024 * 1024;

    [Theory]
    [InlineData("Movie.mkv.parts", 5000 * MB)]
    [InlineData("Show\\Movie.mkv.!qB", 5000 * MB)]
    [InlineData(".hidden\\Movie.mkv", 5000 * MB)]
    [InlineData("sample.mkv", 50 * MB)]
    [InlineData("Show\\Movie-SAMPLE.mkv", 50 * MB)]
    public void IsIgnored_SkipsPartialHiddenAndSamples(string path, long size)
    {
        Assert.True(MediaFileRules.IsIgnored(path, size));
    }

    [Theory]
    [InlineData("Movie.mkv", 5000 * MB)]
    [InlineData("Show\\Season 01\\S01E01.mkv", 1000 * MB)]
    [InlineData("sample.mkv", 300 * MB)]
    [InlineData("Sample Size (2010).mkv", 4000 * MB)]
    public void IsIgnored_KeepsRealFiles(string path, long size)
    {
        Assert.False(MediaFileRules.IsIgnored(path, size));
    }

    [Theory]
    [InlineData("Movie.mkv", true)]
    [InlineData("x.MP4", true)]
    [InlineData("Show\\Season 01\\S01E01.avi", true)]
    [InlineData("Movie.nfo", false)]
    [InlineData("Movie", false)]
    [InlineData("Movie.mkv.parts", false)]
    public void IsVideo_ChecksExtension(string path, bool expected)
    {
        Assert.Equal(expected, MediaFileRules.IsVideo(path));
    }
}
