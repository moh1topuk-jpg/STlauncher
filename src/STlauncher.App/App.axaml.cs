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
            viewModel.RequestHideLauncher += () => window.WindowState = WindowState.Minimized;
            viewModel.RequestCloseLauncher += () => window.Close();

            // The folder picker needs the window; the view model only gets the answer.
            viewModel.PickFolderAsync = async () =>
            {
                var folders = await window.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions { AllowMultiple = false });

                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            };

            window.Opened += (_, _) => SafeInitialize(viewModel);

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
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