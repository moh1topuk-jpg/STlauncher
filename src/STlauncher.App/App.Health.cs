using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using STlauncher.App.Services;
using STlauncher.App.ViewModels;
using STlauncher.App.Views;
using STlauncher.Core;
using STlauncher.Core.Diagnostics;

namespace STlauncher.App;

/// <summary>
/// The launcher noticing that it is itself unwell: an interface that stops answering,
/// and a start that never reaches a usable window.
/// </summary>
public partial class App
{
    /// <summary>
    /// How long a start may take before it is recorded as not having got ready. A usual
    /// start is ready in a second or two; a first one waits for the catalog and the
    /// version list, through mirrors with their own deadlines, and still fits in this.
    /// </summary>
    private static readonly TimeSpan StartupGenerousTime = TimeSpan.FromSeconds(90);

    /// <summary>Null when starts are not tracked: debug builds, --smoke, anything not installed by Velopack.</summary>
    private StartupGuard? _startupGuard;

    /// <summary>Held so the one-shot timer is not collected before it fires.</summary>
    private Timer? _startupStallTimer;

    /// <summary>
    /// Reads how the previous start ended and, if it never got ready, puts the question on
    /// screen instead of the main window. Returns true when it did: the usual start then
    /// follows from the dialog. Nothing is downloaded or replaced without the button.
    /// </summary>
    private bool AskAboutFailedStart(IClassicDesktopStyleApplicationLifetime desktop)
    {
#if DEBUG
        // A debug session is stopped halfway all the time; that is not a failed start.
        return false;
#else
        if (Program.SmokeRun)
        {
            return false;
        }

        try
        {
            // Its own instance: this runs before the services exist, on purpose, so the
            // question does not depend on whatever kept the last start from opening.
            var updates = new UpdateService();

            if (!updates.IsSupported || updates.CurrentVersion is not { Length: > 0 } version)
            {
                return false;
            }

            var guard = StartupGuard.In(CrashLog.DataRoot);
            var previous = guard.ReadPrevious(version);
            _startupGuard = guard;

            if (!previous.Failed)
            {
                return false;
            }

            // From here a kill is not held against the next start: the question would
            // otherwise follow itself.
            guard.MarkAsking();
            updates.UseFeeds(AppSettings.BuiltInUpdateFeeds);

            var language = LoadLanguageForDialog();

            var dialog = new StartupRepairDialog(
                version,
                previous.AlreadyRepaired,
                (progress, cancellation) => updates.PrepareRepairAsync(progress, cancellation),
                () =>
                {
                    guard.MarkRepairing();
                    return updates.ApplyRepairAndRestart();
                },
                () =>
                {
                    if (language is not null)
                    {
                        // The launcher's own service merges the language again in a moment.
                        Resources.MergedDictionaries.Remove(language);
                    }

                    StartLauncher(desktop, showNow: true);
                },
                AppSettings.InstallerMirrorUrl);

            desktop.MainWindow = dialog;
            return true;
        }
        catch (Exception ex)
        {
            // The question is a courtesy. If it cannot be asked, the launcher starts as usual.
            CrashLog.Write(ex, "the repair question could not be shown, the launcher kept starting");
            return false;
        }
#endif
    }

    /// <summary>The dialog's strings, in the player's language, without building the services.</summary>
    private ResourceInclude? LoadLanguageForDialog()
    {
        try
        {
            var language = LocalizationService.Normalize(new SettingsService(LauncherPaths.Default()).Load().Language);
            var baseUri = new Uri("avares://STlauncher.App/");
            var include = new ResourceInclude(baseUri) { Source = new Uri(baseUri, $"Assets/Lang/{language}.axaml") };

            Resources.MergedDictionaries.Add(include);
            return include;
        }
        catch (Exception)
        {
            // The dialog then speaks English from its fallbacks, which beats not asking.
            return null;
        }
    }

    /// <summary>
    /// Starts the freeze watchdog and follows this start until the main window is usable.
    /// </summary>
    private void WatchHealth(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window window,
        MainWindowViewModel viewModel,
        string dataRoot)
    {
        var activity = new ActivitySnapshot();
        viewModel.TrackActivity(activity);

        var watchdog = new FreezeWatchdog(FreezeReportStore.In(dataRoot), activity);

        // From the first frame on: before that there is no interface to be frozen, and a
        // start that never gets here is the other mechanism's business.
        window.Opened += (_, _) => watchdog.Start();

        // After the exit begins nobody answers pings any more, and stopping a server can
        // take most of a minute. That is not a freeze.
        desktop.Exit += (_, _) => watchdog.Dispose();

        if (_startupGuard is not { } guard)
        {
            return;
        }

        void OnReady()
        {
            // Off the interface thread: the write waits for the disk.
            _ = Task.Run(guard.MarkReady);
        }

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsStartupReady) && viewModel.IsStartupReady)
            {
                OnReady();
            }
        };

        // A pool timer, not a dispatcher one: the start that hangs the interface thread is
        // exactly the one this has to catch.
        _startupStallTimer = new Timer(
            _ =>
            {
                if (!viewModel.IsStartupReady)
                {
                    guard.MarkStalled();
                }
            },
            null,
            StartupGenerousTime,
            Timeout.InfiniteTimeSpan);

        // Closed by the player before it got ready: their choice, not a failure.
        desktop.Exit += (_, _) => guard.MarkClosed();
    }
}
