using System.Linq;
using STlauncher.Core.Boost;
using STlauncher.Core.Content;
using STlauncher.Core.Instances;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Where the catalog sync and "Ускорение" meet over the renderer. The switch never puts a
/// second renderer next to one the build has; this is the other direction - the server's
/// build brings Embeddium or Rubidium into a build where the switch had put Sodium. Two
/// renderers crash the game, the server's one is part of the server's build, so the
/// switch's own steps aside: switched off, kept, and written down as parked.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>What to add to the sync's status line; taken once.</summary>
    private string? _syncRendererNote;

    /// <summary>Called for every file the catalog sync has just put into the build.</summary>
    private void StepBoostRendererAside(Instance instance, string gameDirectory, CatalogItem item, string fileName)
    {
        if (instance.Boost is not { Jars.Count: > 0 } record)
        {
            return;
        }

        var parked = BoostRenderer.StepAside(gameDirectory, record, fileName, item.Source.Project, instance.Loader, instance.VersionId);

        if (parked.Count == 0)
        {
            return;
        }

        foreach (var jar in parked)
        {
            RenameSkinModRecord(instance, jar.FileName, jar.FileName + ".disabled", disabled: true);
            _knownModUpdates.Remove(jar.FileName);
            AppendConsole($"[boost] {item.Name} came with the catalog build: switched off {jar.FileName}, kept in mods/ as {jar.FileName}.disabled");
        }

        _instances.Save(instance);

        _syncRendererNote = Localize(
            "Boost_RendererSteppedAside",
            "The build now has {0} from the catalog, so {1} added by “Speed-up” is switched off: two renderers together crash the game. Its file stays in the mods folder.",
            item.Name,
            string.Join(", ", parked.Select(j => j.Title ?? j.FileName)));

        if (IsSelectedBuild(instance))
        {
            RaiseBoostState();
        }
    }

    /// <summary>The status line with the note about the renderer after it, when there is one.</summary>
    private string WithRendererNote(string status)
    {
        if (_syncRendererNote is not { } note)
        {
            return status;
        }

        _syncRendererNote = null;
        return status.Length > 0 ? status + " " + note : note;
    }
}
