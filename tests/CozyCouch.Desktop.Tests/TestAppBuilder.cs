using Avalonia;
using Avalonia.Headless;
using CozyCouch.Desktop;
using CozyCouch.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
// One headless application for the whole assembly. Rebuilding it per test raced the render loop
// (DefaultRenderLoop.Add from the wrong thread) and xunit dropped that test as a cleanup error.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: AssemblyFixture(typeof(HeadlessSetupGuard))]

namespace CozyCouch.Desktop.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UseSkia() // real image decoding, for the PosterImage tests
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        var headless = builder.WindowingSubsystemInitializer;
        return builder
            .UseWindowingSubsystem(() => { HeadlessSetupGuard.Started(); headless?.Invoke(); }, builder.WindowingSubsystemName ?? "Headless")
            .AfterSetup(_ => HeadlessSetupGuard.Finished());
    }
}

/// <summary>
/// Fails the run when the headless application failed to set up. xunit reports that as a
/// "Test Case Cleanup Failure" before the test starts, so the test silently drops out of the
/// count and the run still passes. Throwing at assembly cleanup turns it into a failed run.
/// </summary>
public sealed class HeadlessSetupGuard : IDisposable
{
    private static int _started;
    private static int _finished;

    // Dispatcher.UIThread belongs to the first thread that touches it. Started here, ahead of every test, that's the
    // session's thread. Otherwise a plain [Fact] that happens to run first takes it, the render loop then refuses the
    // session's thread, and every headless test in the assembly is lost.
    public HeadlessSetupGuard() =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessSetupGuard).Assembly)
            .Dispatch(() => { }, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    internal static void Started() => Interlocked.Increment(ref _started);
    internal static void Finished() => Interlocked.Increment(ref _finished);

    public void Dispose()
    {
        int started = Volatile.Read(ref _started), finished = Volatile.Read(ref _finished);
        if (started != finished)
            throw new InvalidOperationException(
                $"The headless application failed to set up {started - finished} time(s); the tests it was meant for never ran.");
    }
}
