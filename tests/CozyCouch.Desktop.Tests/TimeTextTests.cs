using CozyCouch.Desktop.ViewModels;

namespace CozyCouch.Desktop.Tests;

/// <summary>A saved position can come from another PC, so its text must survive any number.</summary>
public sealed class TimeTextTests
{
    [Theory]
    [InlineData(70, "1:10")]
    [InlineData(3730, "1:02:10")]
    [InlineData(-5, "0:00")]
    public void Format_ReadsAsAClock(double seconds, string expected) => Assert.Equal(expected, TimeText.Format(seconds));

    [Theory]
    [InlineData(1e12)]
    [InlineData(1e300)]
    public void Format_ANumberPastAnyClock_Clamps(double seconds) => Assert.Equal("240:00:00", TimeText.Format(seconds));
}
