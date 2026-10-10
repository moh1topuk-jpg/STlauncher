using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;

namespace STlauncher.Core.Skins;

public enum InGameSkinState
{
    /// <summary>The switch is off and nothing stands in its way.</summary>
    Off,

    /// <summary>A build without a mod loader: there is nowhere to put the mod.</summary>
    NoLoader,

    /// <summary>Another skin mod is switched on in the build; the launcher does not add a second one.</summary>
    OtherSkinMod,

    /// <summary>On, with the jar the launcher installed.</summary>
    On,

    /// <summary>On, with a CustomSkinLoader the build already had: the launcher only hands it the skin.</summary>
    OnWithOwnMod,

    /// <summary>
    /// The player asked for it, and has since removed or switched off the jar. The
    /// launcher leaves it that way until the switch is flipped again.
    /// </summary>
    ModRemoved
}

/// <param name="Mod">The skin mod found in the build, when the state is about one.</param>
public sealed record InGameSkinStatus(InGameSkinState State, SkinMod? Mod = null)
{
    /// <summary>The skin is being handed to the game at every launch.</summary>
    public bool IsActive => State is InGameSkinState.On or InGameSkinState.OnWithOwnMod;
}

/// <summary>What a launch found and did.</summary>
/// <param name="State">Where the build stood; the skin was handed over only for the two "on" states.</param>
/// <param name="SkinFile">The copy now in the game folder, or null when no skin is worn.</param>
/// <param name="RecordChanged">True when <see cref="Instance.SkinInGame"/> changed and the instance should be saved.</param>
public sealed record InGameSkinRefresh(InGameSkinState State, string? SkinFile, bool RecordChanged);

/// <summary>What turning the switch on came to.</summary>
public enum InGameSkinEnableResult
{
    /// <summary>The mod was downloaded and put into the build.</summary>
    Installed,

    /// <summary>The jar the launcher had switched off earlier was switched back on.</summary>
    SwitchedBackOn,

    /// <summary>The build has its own CustomSkinLoader; nothing was downloaded.</summary>
    UsingOwnMod,

    NoLoader,
    OtherSkinMod,

    /// <summary>The mod is not published for this build's game version and loader.</summary>
    NoVersion
}

/// <summary>
/// "Show my skin in the game": the worn library skin, on the player's own screen, through
/// the CustomSkinLoader mod.
/// </summary>
/// <remarks>
/// An offline account has no skin as far as the game knows, so the player sees Steve
/// whatever the launcher's figure wears. CustomSkinLoader can take a skin from a file by
/// nickname. The launcher puts the mod into a build when the player asks for it there,
/// and before each launch of that build copies the worn skin where the mod looks.
///
/// What is seen where: the player sees their skin; another player sees it only if their
/// own game asks a skin system that knows this nickname. A file on this computer is not
/// sent to anyone.
///
/// The rules that keep this from fighting the player: nothing is downloaded outside
/// <see cref="EnableAsync"/>, which runs on their click; only the jar the launcher
/// installed is ever switched off, by renaming; a jar the player removed or switched off
/// is not brought back by a launch; a build that has another skin mod is left alone.
/// </remarks>
public sealed class InGameSkin
{
    private const string DisabledSuffix = ".disabled";

    private readonly DownloadClient _downloader;
    private readonly ModrinthClient _modrinth;

    public InGameSkin(DownloadClient downloader, ModrinthClient modrinth)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _modrinth = modrinth ?? throw new ArgumentNullException(nameof(modrinth));
    }

    /// <summary>Where things stand for a build, from its record and its mods folder. Changes nothing.</summary>
    public static InGameSkinStatus Inspect(Instance instance, string gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (!InGameSkinMod.SupportsLoader(instance.Loader))
        {
            return new InGameSkinStatus(InGameSkinState.NoLoader);
        }

        var settings = instance.SkinInGame;

        // The launcher's jar is in place and the switch is on: that is the whole answer,
        // and the one case asked before every launch, so no other jar is opened for it.
        // Another skin mod added beside it later is the player's own doing.
        if (settings is { Enabled: true, Jar: { Length: > 0 } jar } &&
            File.Exists(Path.Combine(ModManager.ModsDirectory(gameDirectory), jar)))
        {
            return new InGameSkinStatus(InGameSkinState.On);
        }

        var foreign = SkinModDetector.Find(gameDirectory, settings?.Jar);

        if (foreign is { IsCustomSkinLoader: false })
        {
            return new InGameSkinStatus(InGameSkinState.OtherSkinMod, foreign);
        }

        if (settings is not { Enabled: true })
        {
            return new InGameSkinStatus(InGameSkinState.Off, foreign);
        }

        return foreign is not null
            ? new InGameSkinStatus(InGameSkinState.OnWithOwnMod, foreign)
            : new InGameSkinStatus(InGameSkinState.ModRemoved);
    }

    /// <summary>
    /// The player turned the switch on: puts the mod into the build unless the build has
    /// one, and remembers what was done in <see cref="Instance.SkinInGame"/>. The caller
    /// saves the instance. Throws when the download fails; the record is then unchanged.
    /// </summary>
    public async Task<InGameSkinEnableResult> EnableAsync(Instance instance, string gameDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (!InGameSkinMod.SupportsLoader(instance.Loader))
        {
            return InGameSkinEnableResult.NoLoader;
        }

        var previousJar = instance.SkinInGame?.Jar;
        var foreign = SkinModDetector.Find(gameDirectory, previousJar);

        if (foreign is { IsCustomSkinLoader: false })
        {
            return InGameSkinEnableResult.OtherSkinMod;
        }

        var mods = ModManager.ModsDirectory(gameDirectory);

        // The launcher's own jar is still there and switched on: only the switch was off.
        if (previousJar is { Length: > 0 } && File.Exists(Path.Combine(mods, previousJar)))
        {
            instance.SkinInGame!.Enabled = true;
            return InGameSkinEnableResult.SwitchedBackOn;
        }

        if (foreign is not null)
        {
            // The build's own CustomSkinLoader does the work. No jar is recorded, so
            // turning the switch off later touches no file of the player's.
            instance.SkinInGame = new InGameSkinSettings { Enabled = true, SkinFile = instance.SkinInGame?.SkinFile };
            return InGameSkinEnableResult.UsingOwnMod;
        }

        // Switched off by the launcher earlier (or by the player since): the same file comes back by its name.
        if (previousJar is { Length: > 0 } && File.Exists(Path.Combine(mods, previousJar + DisabledSuffix)))
        {
            File.Move(Path.Combine(mods, previousJar + DisabledSuffix), Path.Combine(mods, previousJar), overwrite: false);
            instance.SkinInGame!.Enabled = true;
            return InGameSkinEnableResult.SwitchedBackOn;
        }

        var download = await InGameSkinMod.ResolveAsync(_modrinth, instance.VersionId, instance.Loader, cancellationToken).ConfigureAwait(false);

        if (download is null)
        {
            return InGameSkinEnableResult.NoVersion;
        }

        await _downloader
            .EnsureFileAsync(
                new DownloadItem(download.Url, Path.Combine(mods, download.FileName), Size: download.Size, Sha512: download.Sha512),
                cancellationToken)
            .ConfigureAwait(false);

        instance.SkinInGame = new InGameSkinSettings
        {
            Enabled = true,
            Jar = download.FileName,
            ModVersion = download.Version,
            SkinFile = instance.SkinInGame?.SkinFile
        };

        return InGameSkinEnableResult.Installed;
    }

    /// <summary>
    /// The player turned the switch off: the jar the launcher installed is renamed to
    /// <c>.disabled</c>, never deleted, and the mod's config and the skin copy stay as
    /// they are - with the mod off nothing reads them. A CustomSkinLoader that was the
    /// build's own is not touched at all.
    /// </summary>
    /// <returns>The file name that was switched off, or null when no file was touched.</returns>
    public static string? Disable(Instance instance, string gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.SkinInGame is not { } settings)
        {
            return null;
        }

        settings.Enabled = false;

        if (settings.Jar is not { Length: > 0 } jar)
        {
            return null;
        }

        var path = Path.Combine(ModManager.ModsDirectory(gameDirectory), jar);

        if (!File.Exists(path))
        {
            return null;
        }

        // An older switched-off copy under the same name is the same file from an earlier round.
        File.Move(path, path + DisabledSuffix, overwrite: true);
        return jar;
    }

    /// <summary>
    /// Before a launch: the worn skin and its model go where the mod reads them. Does
    /// nothing unless the switch is on and a CustomSkinLoader is actually running in the
    /// build; never downloads.
    /// </summary>
    /// <param name="skinPath">The worn skin's PNG, or null when no library skin is worn: then the launcher's copy is taken away and the mod asks its other sources.</param>
    public static InGameSkinRefresh Refresh(Instance instance, string gameDirectory, string username, string? skinPath, bool slim)
    {
        ArgumentNullException.ThrowIfNull(instance);

        // Asked before every launch of every build: the switch is looked at first, and
        // the mods folder is read only for a build that has it on.
        if (instance.SkinInGame is not { Enabled: true } settings)
        {
            return new InGameSkinRefresh(InGameSkinState.Off, null, false);
        }

        var state = Inspect(instance, gameDirectory).State;

        if (state is not (InGameSkinState.On or InGameSkinState.OnWithOwnMod))
        {
            return new InGameSkinRefresh(state, null, false);
        }

        var data = Path.Combine(gameDirectory, InGameSkinConfig.DataFolder);
        var skins = Path.Combine(data, InGameSkinConfig.SkinFolder.Replace('/', Path.DirectorySeparatorChar));
        var previous = settings.SkinFile;

        // The nickname becomes a file name; the launcher's own rule for nicknames
        // (letters, digits, underscore) is the only kind of name written here.
        string? current = skinPath is not null && File.Exists(skinPath) && Auth.OfflineAuth.IsValidUsername(username)
            ? username + ".png"
            : null;

        if (current is not null)
        {
            WriteConfig(data, slim);
            CopyIfDifferent(skinPath!, Path.Combine(skins, current));
        }

        // The copy left from another nickname, or from a skin no longer worn, would
        // keep showing in the game. It is the launcher's own copy; the library has the skin.
        if (previous is { Length: > 0 } &&
            !string.Equals(previous, current, StringComparison.OrdinalIgnoreCase) &&
            previous.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            TryDelete(Path.Combine(skins, previous));
        }

        settings.SkinFile = current;
        return new InGameSkinRefresh(state, current, !string.Equals(previous, current, StringComparison.Ordinal));
    }

    /// <summary>
    /// Puts the launcher's entry first in the mod's load list. With no config yet - the
    /// game has not run with the mod - the entry is left in the mod's ExtraList folder
    /// instead: on its first start the mod writes its own default list and puts the
    /// waiting entry in front of it, which a config written here could not know to do.
    /// The same when the config cannot be read: the mod sets a damaged one aside itself.
    /// </summary>
    private static void WriteConfig(string dataDirectory, bool slim)
    {
        var configPath = Path.Combine(dataDirectory, InGameSkinConfig.ConfigFileName);

        if (File.Exists(configPath))
        {
            try
            {
                if (InGameSkinConfig.Merge(File.ReadAllText(configPath), slim) is { } merged)
                {
                    AtomicFile.WriteAllText(configPath, merged);
                }

                return;
            }
            catch (JsonException)
            {
                // Not ours to mend; the entry goes the other way round.
            }
        }

        var extra = Path.Combine(dataDirectory, InGameSkinConfig.ExtraListFolder, InGameSkinConfig.ExtraListFileName);
        var text = InGameSkinConfig.ExtraListText(slim);

        if (!File.Exists(extra) || File.ReadAllText(extra) != text)
        {
            AtomicFile.WriteAllText(extra, text);
        }
    }

    /// <summary>
    /// The mod notices a new skin by the file's size and time, so an unchanged skin is
    /// not written again and the game keeps what it already loaded.
    /// </summary>
    private static void CopyIfDifferent(string source, string destination)
    {
        var bytes = File.ReadAllBytes(source);

        if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes))
        {
            return;
        }

        AtomicFile.Write(destination, stream => stream.Write(bytes));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover copy is only a leftover.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
