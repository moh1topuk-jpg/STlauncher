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
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                // Pick up DNS changes during a long-running session instead of pinning the
                // first resolved address for the process lifetime.
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),

                // A host that drops packets instead of refusing must not hold a request
                // for the full five minutes before the launcher tries the next address.
                ConnectTimeout = TimeSpan.FromSeconds(15)
            };

            // Long enough for a large asset download, short enough that a stalled API
            // request cannot hang the UI for half an hour.
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

            // Modrinth's API expects a descriptive agent identifying the app and a contact.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "STlauncher/0.2 (+https://github.com/moh1topuk-jpg/STlauncher)");

            return client;
        });

        services.AddSingleton<MetadataClient>();
        services.AddSingleton<VerifiedFileCache>();
        services.AddSingleton<DownloadClient>();
        services.AddSingleton<VersionService>();
        services.AddSingleton<AssetService>();
        services.AddSingleton<JavaManager>();
        services.AddSingleton<GameLauncher>();
        services.AddSingleton<LoaderService>();
        services.AddSingleton<ModrinthClient>();
        services.AddSingleton<ModManager>();
        services.AddSingleton<ModpackInstaller>();
        services.AddSingleton<ContentCatalogService>();
        services.AddSingleton<STlauncher.Core.Server.ServerStatsClient>();
        services.AddSingleton<CatalogInstaller>();
        services.AddSingleton<LaunchService>();
        services.AddSingleton<SkinService>();
        services.AddSingleton<RemoteImageService>();
        services.AddSingleton<TranslationService>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<STlauncher.Core.Diagnostics.NetworkDiagnostics>();
        services.AddSingleton<InstanceManager>();
        services.AddSingleton<InstanceBackupService>();
        services.AddSingleton<STlauncher.Core.Import.InstanceImporter>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<DiscordPresenceService>();
        services.AddSingleton<UsageReporter>();

        services.AddTransient<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}