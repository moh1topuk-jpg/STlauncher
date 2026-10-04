using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using STlauncher.App.Services;
using STlauncher.App.ViewModels;
using STlauncher.App.Views;

namespace STlauncher.App;

public partial class App : Application
{
    private IServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            _services = LauncherHost.Build();

            // The language must be applied before the window is created so that the
            // first layout already renders translated strings.
            var settings = _services.GetRequiredService<SettingsService>().Load();
            _services.GetRequiredService<ThemeService>().Apply(settings.Theme, settings.Accent);
            _services.GetRequiredService<LocalizationService>().Apply(settings.Language);

            var viewModel = _services.GetRequiredService<MainWindowViewModel>();

            var window = new MainWindow { DataContext = viewModel };

            // The window icon follows the server artwork: the launcher PNG when present,
            // otherwise the icon the server reports.
            viewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.ServerIcon) &&
                    viewModel.ServerIcon is { } icon)
                {
                    window.Icon = new WindowIcon(icon);
                }
            };

            // The game process is independent of the launcher, so hiding or closing the
            // window never terminates Minecraft.
            var tray = CreateTrayIcon(window, viewModel, desktop);

            void ShowWindow()
            {
                tray.IsVisible = false;
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }

            // My server: a running server lives inside the launcher, so every way out of it
            // asks first. The view model puts the question on screen and says "hold".
            // The skin editor: a drawing that was never saved is asked about in the same way.
            // Shutdown() closes the window without asking it, so the tray's "Quit" asks here.
            void Quit()
            {
                if (viewModel.HoldCloseForHosting() || viewModel.HoldCloseForSkinEditor())
                {
                    ShowWindow();
                    return;
                }

                desktop.Shutdown();
            }

            window.Closing += (_, e) =>
            {
                // Only a close of the window itself can be held. When the whole application
                // is going down the answer would be ignored; the exit handler below stops
                // the server in that case.
                if (e.CloseReason == WindowCloseReason.WindowClosing && viewModel.HoldCloseForHosting())
                {
                    e.Cancel = true;

                    // Out of the tray if the launcher was hiding there for the game; a
                    // window already on screen is left as the player arranged it.
                    if (!window.IsVisible || tray.IsVisible)
                    {
                        ShowWindow();
                    }
                }
            };

            void HideToTray()
            {
                // Out of the taskbar, into the tray: still one click away, not in the way.
                RefreshTrayMenu(tray, ShowWindow, Quit);
                tray.IsVisible = true;
                window.Hide();
            }

            viewModel.RequestHideLauncher += HideToTray;
            viewModel.RequestConcealLauncher += HideToTray;
            viewModel.RequestCloseLauncher += () => window.Close();
            viewModel.RequestShowLauncher += ShowWindow;

            // Data moved elsewhere: a fresh process reads the new location, this one quits.
            viewModel.RequestRestartLauncher += () =>
            {
                try
                {
                    if (Environment.ProcessPath is { Length: > 0 } exe)
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                    }
                }
                catch (Exception)
                {
                    // The launcher still quits; the player starts it by hand.
                }

                desktop.Shutdown();
            };

            // The folder picker needs the window; the view model only gets the answer.
            viewModel.PickFolderAsync = async () =>
            {
                var folders = await window.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions { AllowMultiple = false });

                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            };

            window.Opened += (_, _) => SafeInitialize(viewModel);

            if (Program.SmokeRun)
            {
                var smoke = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                smoke.Tick += (_, _) =>
                {
                    smoke.Stop();
                    Console.WriteLine($"smoke: window {window.ClientSize.Width:F0}x{window.ClientSize.Height:F0}, ready={viewModel.IsStartupReady}");
                    desktop.Shutdown(viewModel.IsStartupReady ? 0 : 2);
                };
                smoke.Start();
            }

            // Discord shows the last presence until the client says goodbye.
            desktop.Exit += (_, _) => _services.GetRequiredService<DiscordPresenceService>().Dispose();

            // My server: an exit that asked nobody (a restart after moving the data, Windows
            // shutting down) still stops the server in order, so the world is saved.
            desktop.Exit += (_, _) => viewModel.StopHostingForExit(TimeSpan.FromSeconds(40));

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The tray icon the window folds into while the game runs. Created hidden; shown by
    /// the hide handlers and hidden again when the window comes back.
    /// </summary>
    private TrayIcon CreateTrayIcon(MainWindow window, MainWindowViewModel viewModel, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var tray = new TrayIcon
        {
            Icon = window.Icon,
            ToolTipText = "STlauncher",
            IsVisible = false
        };

        tray.Clicked += (_, _) => viewModel.ShowFromTray();

        TrayIcon.SetIcons(this, new TrayIcons { tray });
        return tray;
    }

    /// <summary>Rebuilt on every hide so the labels follow the interface language.</summary>
    private static void RefreshTrayMenu(TrayIcon tray, Action show, Action quit)
    {
        var showItem = new NativeMenuItem(MainWindowViewModel.Localize("Tray_Show", "Show the launcher"));
        showItem.Click += (_, _) => show();

        var quitItem = new NativeMenuItem(MainWindowViewModel.Localize("Tray_Quit", "Quit the launcher"));
        quitItem.Click += (_, _) => quit();

        tray.Menu = new NativeMenu { Items = { showItem, new NativeMenuItemSeparator(), quitItem } };
    }

    /// <summary>
    /// Initialization touches settings, the catalog and the network. It used to run from an
    /// async void handler with no guard, so any failure became an unhandled exception with
    /// no diagnostic at all.
    /// </summary>
    private static async void SafeInitialize(MainWindowViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            viewModel.ReportStartupFailure(ex);
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}