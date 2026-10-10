using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Loaders;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Skins;

/// <summary>One CustomSkinLoader jar to fetch: where from, and the hash it must have.</summary>
public sealed record InGameSkinDownload(string Version, string FileName, string Url, string Sha512, long Size);

/// <summary>
/// Which CustomSkinLoader jar fits a build. The mod is published by its author on
/// Modrinth (project idMHQ4n2), which states a SHA-512 for every file; the launcher asks
/// there for the newest release for the build's loader and game version, and falls back
/// to one pinned release when Modrinth's API cannot be reached.
/// </summary>
public static class InGameSkinMod
{
    public const string ProjectId = "idMHQ4n2";

    public const string ProjectSlug = "customskinloader";

    public const string DisplayName = "CustomSkinLoader";

    /// <summary>Files are only ever taken from Modrinth's own file host.</summary>
    private const string TrustedHost = "https://cdn.modrinth.com/";

    /// <summary>
    /// 15.0.1, the "universal" jar: one file for Fabric, Quilt, Forge and NeoForge. Hash
    /// and size as Modrinth lists them; the same file is on the author's GitHub releases.
    /// </summary>
    public static readonly InGameSkinDownload Pinned = new(
        "15.0.1",
        "CustomSkinLoader_Universal-15.0.1.jar",
        "https://cdn.modrinth.com/data/idMHQ4n2/versions/OLaesh5y/CustomSkinLoader_Universal-15.0.1.jar",
        "8c65193c46c1435ddea571901f1dd04cc179179f59f1c1a0449349de04722b9c1b6e5efaec166975e05d2fa0b05f6c07c9d5d93a05f1c9a57fece2a0d0fc1ce7",
        218215);

    /// <summary>The game versions the pinned release is published for.</summary>
    private static readonly HashSet<string> PinnedGameVersions = new(StringComparer.Ordinal)
    {
        "1.8", "1.8.8", "1.8.9", "1.9", "1.9.4", "1.10", "1.10.2", "1.11", "1.11.2", "1.12", "1.12.1", "1.12.2",
        "1.13.2", "1.14", "1.14.1", "1.14.2", "1.14.3", "1.14.4", "1.15", "1.15.1", "1.15.2",
        "1.16", "1.16.1", "1.16.2", "1.16.3", "1.16.4", "1.16.5", "1.17", "1.17.1", "1.18", "1.18.1", "1.18.2",
        "1.19", "1.19.1", "1.19.2", "1.19.3", "1.19.4", "1.20", "1.20.1", "1.20.2", "1.20.3", "1.20.4", "1.20.5", "1.20.6",
        "1.21", "1.21.1", "1.21.2", "1.21.3", "1.21.4", "1.21.5", "1.21.6", "1.21.7", "1.21.8", "1.21.9", "1.21.10", "1.21.11",
        "26.1", "26.1.1", "26.1.2", "26.2"
    };

    /// <summary>The mod needs a mod loader; a plain game has nowhere to put it.</summary>
    public static bool SupportsLoader(LoaderKind loader)
        => loader is LoaderKind.Fabric or LoaderKind.Quilt or LoaderKind.Forge or LoaderKind.NeoForge;

    /// <summary>The pinned release, when it is published for this loader and game version.</summary>
    public static InGameSkinDownload? PinnedFor(string? gameVersion, LoaderKind loader)
        => SupportsLoader(loader) && gameVersion is not null && PinnedGameVersions.Contains(gameVersion)
            ? Pinned
            : null;

    /// <summary>
    /// The file to install out of Modrinth's answer: the newest release that names this
    /// game version and loader. A file without a SHA-512, from another host, or with a
    /// name that is not a plain jar name is not taken.
    /// </summary>
    public static InGameSkinDownload? Pick(IEnumerable<ModVersion> versions, string? gameVersion, LoaderKind loader)
    {
        if (!SupportsLoader(loader) || string.IsNullOrEmpty(gameVersion) || ModrinthClient.ToModrinthLoader(loader) is not { } loaderName)
        {
            return null;
        }

        var fitting = versions
            .Where(v => v.GameVersions.Contains(gameVersion, StringComparer.Ordinal))
            .Where(v => v.Loaders.Contains(loaderName, StringComparer.OrdinalIgnoreCase))
            .Where(v => string.Equals(v.VersionType, "release", StringComparison.OrdinalIgnoreCase));

        foreach (var version in fitting)
        {
            if (version.PrimaryFile is { } file && IsAcceptable(file))
            {
                return new InGameSkinDownload(DisplayVersion(version.VersionNumber), file.FileName, file.Url, file.Sha512!, file.Size);
            }
        }

        return null;
    }

    /// <summary>
    /// Asks Modrinth, and answers from the pinned release only when Modrinth could not be
    /// asked. When it answered that there is nothing for this build, that is the answer:
    /// null, and the player is told the mod does not exist for their version.
    /// </summary>
    public static async Task<InGameSkinDownload?> ResolveAsync(
        ModrinthClient modrinth,
        string? gameVersion,
        LoaderKind loader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modrinth);

        if (!SupportsLoader(loader) || string.IsNullOrEmpty(gameVersion))
        {
            return null;
        }

        // The shared client waits minutes, which suits a large download; a list of
        // versions that has not come in a few seconds is not coming.
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        patience.CancelAfter(TimeSpan.FromSeconds(12));

        try
        {
            var versions = await modrinth.GetVersionsAsync(ProjectId, gameVersion, loader, patience.Token).ConfigureAwait(false);
            return Pick(versions, gameVersion, loader);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return PinnedFor(gameVersion, loader);
        }
    }

    private static bool IsAcceptable(ModFile file)
        => !string.IsNullOrEmpty(file.Sha512) &&
           file.Url.StartsWith(TrustedHost, StringComparison.Ordinal) &&
           file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
           file.FileName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 &&
           file.FileName.IndexOfAny(new[] { '/', '\\' }) < 0 &&
           !file.FileName.StartsWith('.');

    /// <summary>"15.0.1-Universal" is the same release as "15.0.1"; the suffix only names the jar flavour.</summary>
    private static string DisplayVersion(string versionNumber)
    {
        var dash = versionNumber.IndexOf('-');
        return dash > 0 ? versionNumber[..dash] : versionNumber;
    }
}
