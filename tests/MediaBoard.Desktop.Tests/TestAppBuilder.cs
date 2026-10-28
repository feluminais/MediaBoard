using Avalonia;
using Avalonia.Headless;
using MediaBoard.Desktop;
using MediaBoard.Desktop.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
// One headless application for the whole assembly. Rebuilding it per test raced the render loop
// (DefaultRenderLoop.Add from the wrong thread) and xunit dropped that test as a cleanup error.
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: AssemblyFixture(typeof(HeadlessSetupGuard))]

namespace MediaBoard.Desktop.Tests;

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
