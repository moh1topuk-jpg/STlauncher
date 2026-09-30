using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using STlauncher.Core.Content;
using STlauncher.Core.Modpacks;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// A build as a file someone else can open. Mods, packs and shaders Modrinth knows go in
/// by address and hash - a few kilobytes each - and everything else rides inside. The
/// result is an ordinary .mrpack: this launcher imports it by drag and drop, and so do
/// Prism and the Modrinth App.
/// </summary>
public partial class MainWindowViewModel
{
    private sealed record ExportCandidate(string RelativePath, string FullPath, long Size, string Sha1, string Sha512);

    public async Task ExportModpackAsync(string targetPath)
    {
        if (SelectedInstance is not { } instance || IsModsBusy)
        {
            return;
        }

        var gameVersion = instance.VersionId ?? SelectedVersion?.Id;

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            Status = Localize("Export_NoVersion", "Pick the game version in the build settings first: a modpack has to name it");
            return;
        }

        try
        {
            IsModsBusy = true;
            Status = Localize("Export_Hashing", "Export: reading the build's files…");

            var directory = InstanceDirectory;
            var loaderVersion = instance.LoaderVersion ?? SelectedLoaderVersion?.Version;
            var candidates = await Task.Run(() => CollectExportCandidates(directory));

            Status = Localize("Export_Lookup", "Export: checking {0} file(s) against Modrinth…", candidates.Count);

            IReadOnlyDictionary<string, ModVersion> known;

            try
            {
                known = await _modrinth.GetVersionsByHashesAsync(candidates.Select(c => c.Sha1));
            }
            catch (Exception ex)
            {
                // Without Modrinth every file simply travels inside the pack.
                known = new Dictionary<string, ModVersion>();
                AppendConsole($"[export] Modrinth did not answer, packing every file inside: {ex.Message}");
            }

            var files = new List<ModpackExportFile>();
            var overrides = new List<ModpackOverride>();

            foreach (var candidate in candidates)
            {
                var file = known.TryGetValue(candidate.Sha1, out var version)
                    ? version.Files.FirstOrDefault(f => string.Equals(f.Sha1, candidate.Sha1, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (file is not null && ModpackWriter.IsAllowedDownload(file.Url))
                {
                    files.Add(new ModpackExportFile(candidate.RelativePath, file.Url, candidate.Sha1, candidate.Sha512, candidate.Size));
                }
                else
                {
                    overrides.Add(new ModpackOverride(candidate.RelativePath, candidate.FullPath));
                }
            }

            var carried = overrides.Count;

            // Mod settings are part of what makes the build play the way it does.
            var config = Path.Combine(directory, "config");

            if (Directory.Exists(config))
            {
                foreach (var path in Directory.EnumerateFiles(config, "*", SearchOption.AllDirectories))
                {
                    overrides.Add(new ModpackOverride("config/" + Path.GetRelativePath(config, path).Replace('\\', '/'), path));
                }
            }

            Status = Localize("Export_Writing", "Export: writing the modpack…");
            var name = instance.Name;
            var loader = instance.Loader;

            await Task.Run(() => ModpackWriter.Write(targetPath, name, "1.0.0", gameVersion!, loader, loaderVersion, files, overrides));

            var size = FormatSize(new FileInfo(targetPath).Length);
            Status = Localize("Export_Done", "Modpack saved: {0} by Modrinth address, {1} inside the file ({2})", files.Count, carried, size);
            AppendConsole($"[export] {instance.Name} -> {targetPath}: {files.Count} by address, {carried} carried, {overrides.Count - carried} config file(s), {size}");
        }
        catch (Exception ex)
        {
            Status = Localize("Export_Failed", "Could not export the build: {0}", ex.Message);
            AppendConsole($"[export] failed: {ex}");
        }
        finally
        {
            IsModsBusy = false;
        }
    }

    /// <summary>Enabled mods, and the packs and shaders kept as archives. Switched-off mods stay home.</summary>
    private List<ExportCandidate> CollectExportCandidates(string directory)
    {
        var result = new List<ExportCandidate>();

        void Add(string folder, string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                var sha1 = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
                stream.Position = 0;
                var sha512 = Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();

                result.Add(new ExportCandidate(folder + "/" + Path.GetFileName(path), path, stream.Length, sha1, sha512));
            }
            catch (Exception)
            {
                // Unreadable right now: left out of the pack.
            }
        }

        foreach (var mod in _mods.ListMods(directory).Where(m => m.Enabled))
        {
            Add(ModManager.ModsFolderName, mod.Path);
        }

        foreach (var folder in new[] { CatalogPlacement.ResourcePacksFolder, CatalogPlacement.ShaderPacksFolder })
        {
            var packs = Path.Combine(directory, folder);

            if (!Directory.Exists(packs))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(packs, "*.zip", SearchOption.TopDirectoryOnly))
            {
                Add(folder, path);
            }
        }

        return result;
    }
}
