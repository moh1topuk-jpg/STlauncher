using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
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
            WriteCrashLog(e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog(e.Exception);
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

    private static void WriteCrashLog(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "STlauncher");

            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing left to try - a failing crash logger must not become the crash.
        }
    }
}