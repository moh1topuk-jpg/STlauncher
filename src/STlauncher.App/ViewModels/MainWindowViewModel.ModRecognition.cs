using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// A jar the player dropped into a build has no origin on record, so the catalog does not
/// mark it as "in the build", an install that needs it brings a second copy, and a build
/// code cannot name it. The update check already hashes every mod and asks Modrinth what
/// each file is; this takes that answer, asks CurseForge about the rest, and writes what
/// was learned into the launcher's own record of the file. The jars are only read.
/// </summary>
public partial class MainWindowViewModel
{
    private ModFileRecognizer? _modRecognizer;

    private bool _isIdentifyingMods;

    private async Task IdentifyDroppedModsAsync(
        Instance instance,
        IReadOnlyDictionary<string, InstalledModItem> byHash,
        IReadOnlyDictionary<string, ModVersion> modrinthKnown)
    {
        if (_isIdentifyingMods)
        {
            return;
        }

        var unknown = byHash
            .Where(p => ModFileRecognizer.NeedsIdentifying(p.Value.Record))
            .Select(p => new UnidentifiedFile(p.Value.FileName, p.Value.Path) { Sha1 = p.Key })
            .ToList();

        if (unknown.Count == 0)
        {
            return;
        }

        try
        {
            _isIdentifyingMods = true;

            _modRecognizer ??= new ModFileRecognizer(_modrinth, _modrinth, _curseForge, _curseForge);
            var useCurseForge = IsCurseForgeAvailable;

            // Fingerprinting the jars Modrinth does not know is disk work.
            var found = await Task.Run(() => _modRecognizer.IdentifyAsync(unknown, modrinthKnown, useCurseForge));
            var stamped = new List<string>();

            foreach (var file in found)
            {
                var existing = instance.InstalledMods.FirstOrDefault(m =>
                    string.Equals(m.FileName, file.FileName, StringComparison.OrdinalIgnoreCase));

                // Installed from the catalog or a site while the answer was coming: that record knows better.
                if (!ModFileRecognizer.NeedsIdentifying(existing))
                {
                    continue;
                }

                instance.InstalledMods.RemoveAll(m =>
                    string.Equals(m.FileName, file.FileName, StringComparison.OrdinalIgnoreCase));
                instance.InstalledMods.Add(file.ToRecord(existing));
                stamped.Add($"{file.FileName} = {file.Project.Title} ({SourceName(file.Source)})");
            }

            if (stamped.Count == 0)
            {
                return;
            }

            _instances.Save(instance);
            AppendConsole($"[mods] recognised {stamped.Count} file(s) added by hand: {string.Join("; ", stamped)}");

            if (IsSelectedBuild(instance))
            {
                RefreshMods();
                RefreshBrowserInstallState();
                RefreshHiddenItems();
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[mods] could not recognise the files added by hand: {ex.Message}");
        }
        finally
        {
            _isIdentifyingMods = false;
        }
    }
}
