using Avalonia.Threading;

namespace CozyCouch.Desktop.Tests.Support;

/// <summary>Helpers for tests that wait on work the view models do on the thread pool.</summary>
internal static class Ui
{
    public static void Flush() => Dispatcher.UIThread.RunJobs();

    /// <summary>Pumps the UI thread until <paramref name="done"/> holds, so queued results get applied.</summary>
    public static async Task WaitUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The view model never caught up.");
            await Task.Delay(10);
            Flush();
        }
    }
}
