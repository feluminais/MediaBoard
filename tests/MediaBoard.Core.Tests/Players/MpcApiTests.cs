using MediaBoard.Core.Players.MpcHc;

namespace MediaBoard.Core.Tests.Players;

public class MpcApiTests
{
    [Fact]
    public void SplitPayload_HonoursEscapedPipe()
    {
        Assert.Equal(["a|b", "c"], MpcApi.SplitPayload(@"a\|b|c"));
    }

    [Fact]
    public void SplitPayload_KeepsEmptyFieldsAndPlainBackslashes()
    {
        Assert.Equal(["", "", @"C:\x\y.mkv", "5"], MpcApi.SplitPayload(@"||C:\x\y.mkv|5"));
    }

    [Fact]
    public void ParseNowPlaying_ReadsDuration()
    {
        var nowPlaying = MpcApi.ParseNowPlaying(@"Title|Author|Description|C:\Videos\test.mp4|2735.104");

        Assert.Equal(TimeSpan.FromSeconds(2735.104), nowPlaying.Duration);
        Assert.Equal("Title", nowPlaying.Title);
        Assert.Equal("Author", nowPlaying.Author);
        Assert.Equal("Description", nowPlaying.Description);
    }

    [Fact]
    public void ParseNowPlaying_KeepsNonAsciiAndBackslashesAsTheyAre()
    {
        const string path = @"G:\Torrent Downloads\Brassic - S01E01 (2019) [Цікава Ідея].mkv";
        Assert.Equal(path, MpcApi.ParseNowPlaying($"Brassic|||{path}|2735.104").FileName);

        var nowPlaying = MpcApi.ParseNowPlaying(@"Bob’s \ Burgers\x|||C:\Відео\Bob’s.mkv|1.500");

        Assert.Equal(@"Bob’s \ Burgers\x", nowPlaying.Title);
        Assert.Equal(@"C:\Відео\Bob’s.mkv", nowPlaying.FileName);
    }

    [Fact]
    public void ParseNowPlaying_EmptyDuration_IsZero()
    {
        Assert.Equal(TimeSpan.Zero, MpcApi.ParseNowPlaying(@"t|||C:\v.mkv|").Duration);
    }

    [Fact]
    public void ParseNowPlaying_TooFewFields_Throws()
    {
        Assert.Throws<FormatException>(() => MpcApi.ParseNowPlaying("only|three|fields"));
    }

    [Fact]
    public void FormatSeconds_UsesInvariantDecimalPoint()
    {
        Assert.Equal("754.5", MpcApi.FormatSeconds(TimeSpan.FromSeconds(754.5)));
        Assert.Equal("10", MpcApi.FormatSeconds(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void TryParseSeconds_ReadsFractionalSeconds()
    {
        Assert.True(MpcApi.TryParseSeconds("12.345", out var at));
        Assert.Equal(TimeSpan.FromSeconds(12.345), at);
        Assert.False(MpcApi.TryParseSeconds("", out _));
    }
}
