using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using STlauncher.App.ViewModels;
using STlauncher.Core;
using STlauncher.Core.Assets;
using STlauncher.Core.Backups;
using STlauncher.Core.Content;
using STlauncher.Core.Http;
using STlauncher.Core.Instances;
using STlauncher.Core.Java;
using STlauncher.Core.Launch;
using STlauncher.Core.Loaders;
using STlauncher.Core.Metadata;
using STlauncher.Core.Mods;
using STlauncher.Core.Modpacks;
using STlauncher.Core.Versions;

namespace STlauncher.App.Services;

public static class LauncherHost
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        var paths = LauncherPaths.Default();
        paths.EnsureCreated();
        services.AddSingleton(paths);

        var settingsService = new SettingsService(paths);
        services.AddSingleton(settingsService);
        var startupSettings = settingsService.Load();

        services.AddLogging(builder =>
        {
#if DEBUG
            builder.AddDebug();
#endif
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton(_ =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("STlauncher/0.1.0");
            return client;
        });

        services.AddSingleton<MetadataClient>();
        services.AddSingleton<DownloadClient>();
        services.AddSingleton<VersionService>();
        services.AddSingleton<AssetService>();
        services.AddSingleton<JavaManager>();
        services.AddSingleton<GameLauncher>();
        services.AddSingleton<LoaderService>();
        services.AddSingleton<ModrinthClient>();
        services.AddSingleton(sp => new CurseForgeClient(
            sp.GetRequiredService<HttpClient>(),
            startupSettings.CurseForgeApiKey));
        services.AddSingleton<ModManager>();
        services.AddSingleton<ModpackInstaller>();
        services.AddSingleton<ContentCatalogService>();
        services.AddSingleton<CatalogInstaller>();
        services.AddSingleton<LaunchService>();
        services.AddSingleton<SkinService>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<InstanceManager>();
        services.AddSingleton<InstanceBackupService>();
        services.AddSingleton<UpdateService>();

        services.AddTransient<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}