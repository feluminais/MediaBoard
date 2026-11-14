using CozyCouch.Core.Playback;

namespace CozyCouch.Core.Tests.Playback;

public sealed class WatchRulesTests
{
    [Theory]
    [InlineData(5400, 6000, true)]
    [InlineData(5399, 6000, false)]
    [InlineData(6000, 6000, true)]
    [InlineData(0, 6000, false)]
    [InlineData(100, 0, false)] // unknown duration
    public void IsWatched_NinetyPercent(double position, double duration, bool expected) =>
        Assert.Equal(expected, WatchRules.IsWatched(position, duration));

    [Theory]
    [InlineData(5385.0, 6000, true)]  // 10% + 15 s from the end
    [InlineData(5384.0, 6000, false)]
    [InlineData(600.0, 6000, false)]  // stopped early
    [InlineData(40.0, 60, true)]      // a clip: 10% + 15 s is 21 s of its 60
    [InlineData(30.0, 60, false)]
    [InlineData(100.0, 0, true)]      // no duration known
    public void EndedCountsAsWatched_OnlyNearTheEnd(double position, double duration, bool expected) =>
        Assert.Equal(expected, WatchRules.EndedCountsAsWatched(position, duration));

    [Fact]
    public void EndedCountsAsWatched_NoPositionKnown() => Assert.True(WatchRules.EndedCountsAsWatched(null, 6000));
}
