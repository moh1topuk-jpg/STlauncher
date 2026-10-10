using System;
using System.Threading.Tasks;
using Avalonia;
using STlauncher.App.Services;
using Velopack;

namespace STlauncher.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    /// <summary>
    /// Started with --smoke: the launcher shows its window, waits a few seconds and exits 0.
    /// CI runs the Linux build this way under a virtual display, so a port that crashes on
    /// start is caught before anyone downloads it.
    /// </summary>
    public static bool SmokeRun { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        SmokeRun = Array.Exists(args, a => a == "--smoke");
        VelopackApp.Build().Run();

        // Without these, a failure outside the UI's try/catch disappears with the process
        // and the user just sees the launcher vanish.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write(e.ExceptionObject as Exception, e.IsTerminating ? "unhandled, the launcher closed" : "unhandled");

        // Not fatal: the task failed and nobody was waiting for it. Still written, because
        // it is usually a real bug that only shows as "something did not refresh".
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "unobserved task, the launcher kept running");
            e.SetObserved();
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // The launcher draws a few dozen small textures; the default cache is sized
            // for a game. A smaller one keeps the working set down on weak machines.
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 96L * 1024 * 1024 })
            .WithInterFont()
            .LogToTrace();
}