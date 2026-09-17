using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
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

            // The game process is independent of the launcher, so hiding or closing the
            // window never terminates Minecraft.
            viewModel.RequestHideLauncher += () => window.WindowState = WindowState.Minimized;
            viewModel.RequestCloseLauncher += () => window.Close();

            window.Opened += async (_, _) => await viewModel.InitializeAsync();

            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
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