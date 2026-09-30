using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Modpacks;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// A build handed over as one line of text. "Copy the build code" puts it in the
/// clipboard; the other player picks "From a build code" and the build appears with
/// its mods downloaded. No file to attach, no server between them; what Modrinth does
/// not host is named so nobody wonders where it went.
/// </summary>
public partial class MainWindowViewModel
{
    [RelayCommand]
    private async Task CopyBuildCodeAsync()
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
            var candidates = await Task.Run(() => CollectExportCandidates(directory));

            Status = Localize("Export_Lookup", "Export: checking {0} file(s) against Modrinth…", candidates.Count);
            var known = await _modrinth.GetVersionsByHashesAsync(candidates.Select(c => c.Sha1));

            var files = new List<BuildCodeFile>();
            var missing = new List<string>();

            foreach (var candidate in candidates)
            {
                var file = known.TryGetValue(candidate.Sha1, out var version)
                    ? version.Files.FirstOrDefault(f => string.Equals(f.Sha1, candidate.Sha1, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (file is not null && ModpackWriter.IsAllowedDownload(file.Url))
                {
                    files.Add(new BuildCodeFile(candidate.RelativePath, file.Url, candidate.Sha1, candidate.Size));
                }
                else
                {
                    missing.Add(Path.GetFileName(candidate.RelativePath));
                }
            }

            var code = BuildCode.Encode(new BuildCodePayload(
                instance.Name,
                gameVersion!,
                instance.Loader,
                instance.LoaderVersion ?? SelectedLoaderVersion?.Version,
                files,
                missing));

            await CopyToClipboardAsync(code);
            AppendConsole($"[code] {instance.Name}: {files.Count} file(s) by address, {missing.Count} not on Modrinth, {code.Length} characters");

            Status = missing.Count == 0
                ? Localize("Code_Copied", "Build code copied: {0} file(s), {1} characters. Paste it to a friend.", files.Count, code.Length)
                : Localize("Code_CopiedPartial", "Build code copied: {0} file(s). Not on Modrinth, so not in the code: {1}", files.Count, string.Join(", ", missing));
        }
        catch (Exception ex)
        {
            Status = Localize("Code_Failed", "Could not make the build code: {0}", ex.Message);
            AppendConsole($"[code] failed: {ex}");
        }
        finally
        {
            IsModsBusy = false;
        }
    }

    /// <summary>Reads a code from the clipboard and makes a new build out of it.</summary>
    [RelayCommand]
    private async Task AddBuildFromCodeAsync()
    {
        if (IsBusy || IsModsBusy)
        {
            return;
        }

        string? text;

        try
        {
            text = await ReadClipboardAsync();
        }
        catch (Exception ex)
        {
            Status = Localize("Code_Failed", "Could not make the build code: {0}", ex.Message);
            return;
        }

        if (!BuildCode.TryDecode(text, out var payload) || payload is null)
        {
            Status = Localize("Code_NotFound", "The clipboard holds no build code. Copy the line that starts with STB1. and try again.");
            return;
        }

        var temporary = Path.Combine(Path.GetTempPath(), $"stlauncher-code-{Guid.NewGuid():N}.mrpack");

        try
        {
            // The code is a modpack without the files inside; the importer takes it from here.
            ModpackWriter.Write(
                temporary,
                payload.Name,
                "1.0.0",
                payload.GameVersion,
                payload.Loader,
                payload.LoaderVersion,
                payload.Files.Select(f => new ModpackExportFile(f.Path, f.Url, f.Sha1, string.Empty, f.Size)).ToList(),
                Array.Empty<ModpackOverride>());

            var instance = _instances.Create(UniqueInstanceName(payload.Name));
            instance.VersionId = payload.GameVersion;
            instance.Loader = payload.Loader;
            instance.LoaderVersion = payload.LoaderVersion;
            _instances.Save(instance);

            _allInstances.Add(instance);
            ApplyBuildFilter();
            SelectedInstance = instance;
            Section = ShellSection.Builds;
            BuildTab = BuildTab.Mods;

            AppendConsole($"[code] new build '{instance.Name}': {payload.Files.Count} file(s) to download, {payload.Missing.Count} not in the code");
            await ImportModpackAsync(temporary);

            if (payload.Missing.Count > 0)
            {
                Status = Localize("Code_AddedPartial", "Build “{0}” added. Not in the code, add by hand: {1}", instance.Name, string.Join(", ", payload.Missing));
            }
            else
            {
                Status = Localize("Code_Added", "Build “{0}” added from the code", instance.Name);
            }
        }
        catch (Exception ex)
        {
            Status = Localize("Code_Failed", "Could not make the build code: {0}", ex.Message);
            AppendConsole($"[code] failed: {ex}");
        }
        finally
        {
            try { File.Delete(temporary); } catch (Exception) { }
        }
    }
}
