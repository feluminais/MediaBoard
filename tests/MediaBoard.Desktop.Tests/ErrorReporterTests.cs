using Avalonia.Headless.XUnit;
using MediaBoard.Core.Activity;
using MediaBoard.Desktop.Tests.Support;
using MediaBoard.Desktop.ViewModels;

namespace MediaBoard.Desktop.Tests;

public sealed class ErrorReporterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mediaboard-errors-").FullName;
    private readonly ActivityHub _hub = new();

    private string LogPath => Path.Combine(_dir, "error.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Report_AppendsToErrorLog_AndLogsToTheActivityPanel()
    {
        var reporter = new ErrorReporter(_hub, LogPath, () => null);

        reporter.Report(new InvalidOperationException("first"), "UI thread");
        reporter.Report(new IOException("second"), "background task");

        var text = File.ReadAllText(LogPath);
        Assert.Contains("UI thread", text);
        Assert.Contains("System.InvalidOperationException: first", text);
        Assert.Contains("System.IO.IOException: second", text);
        var entry = _hub.Recent[^1];
        Assert.Equal(ActivityLevel.Error, entry.Level);
        Assert.Equal("Something went wrong: second", entry.Message);
        Assert.Equal("Details are in error.log in the MediaBoard data folder.", entry.Hint);
    }

    [Fact]
    public void Report_NeverWritesTheTmdbKey()
    {
        var reporter = new ErrorReporter(_hub, LogPath, () => "0123456789abcdef");

        reporter.Report(new HttpRequestException("GET https://api.themoviedb.org/3/movie/1?api_key=0123456789abcdef&language=en-US failed"), "background task");

        var text = File.ReadAllText(LogPath);
        Assert.DoesNotContain("0123456789abcdef", text);
        Assert.DoesNotContain("0123456789abcdef", _hub.Recent[^1].Message);
    }

    [Fact]
    public void Report_WhenTheLogCannotBeWritten_StillLogsToTheActivityPanel()
    {
        var reporter = new ErrorReporter(_hub, Path.Combine(_dir, "missing", "sub", "error.log"), () => null);
        Directory.CreateDirectory(Path.Combine(_dir, "missing"));
        File.WriteAllText(Path.Combine(_dir, "missing", "sub"), "a file where the folder should be");

        reporter.Report(new InvalidOperationException("boom"), "UI thread");

        Assert.Equal("Something went wrong: boom", _hub.Recent[^1].Message);
    }

    [AvaloniaFact]
    public async Task BackgroundQuery_ApplyThatThrows_IsLogged_NotLost()
    {
        var query = new BackgroundQuery(_hub, "Home");

        await query.RunAsync(() => 1, _ => throw new InvalidOperationException("bad row"));
        Ui.Flush();

        var entry = Assert.Single(_hub.Recent);
        Assert.Equal(ActivityLevel.Error, entry.Level);
        Assert.Equal("Couldn't load Home: bad row", entry.Message);
    }
}
