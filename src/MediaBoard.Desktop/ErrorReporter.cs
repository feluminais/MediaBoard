using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MediaBoard.Core.Activity;

namespace MediaBoard.Desktop;

/// <summary>
/// The last-chance handler: an exception nothing else caught (on the UI thread, on any thread, or in a task nobody
/// awaited) is appended to error.log in the data folder and shown in the activity panel. The TMDB key never reaches
/// either: the saved key and any api_key parameter are masked first.
/// </summary>
public sealed partial class ErrorReporter(ActivityHub hub, string logPath, Func<string?> tmdbKey)
{
    private const string Hint = "Details are in error.log in the MediaBoard data folder.";

    private readonly Lock _gate = new();
    private bool _installed;

    /// <summary>Hooks the three unhandled-exception events. Call once, after the services are built.</summary>
    public void Install()
    {
        lock (_gate)
        {
            if (_installed) return;
            _installed = true;
        }

        // The window stays usable: the problem shows on the status bar instead of closing the app.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Report(e.Exception, "UI thread");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Report(ex, e.IsTerminating ? "background thread, closing" : "background thread");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Report(e.Exception, "background task");
            e.SetObserved();
        };
    }

    /// <summary>Writes the exception to error.log and logs it to the activity panel. Never throws.</summary>
    public void Report(Exception exception, string source)
    {
        var details = Mask(exception.ToString());
        try
        {
            lock (_gate)
            {
                File.AppendAllText(logPath,
                    string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.Now:O} ({source}){Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The activity panel still gets it.
        }

        hub.Log(ActivityLevel.Error, $"Something went wrong: {Mask(exception.Message)}", Hint);
    }

    private string Mask(string text)
    {
        text = ApiKeyParameter().Replace(text, "api_key=***");
        string? key;
        try
        {
            key = tmdbKey()?.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            key = null;
        }

        return string.IsNullOrEmpty(key) ? text : text.Replace(key, "***", StringComparison.Ordinal);
    }

    [GeneratedRegex("api_key=[^&\\s\"']+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyParameter();
}
