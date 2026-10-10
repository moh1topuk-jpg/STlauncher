using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;
using STlauncher.Core.Mods;
using STlauncher.Core.Packs;

namespace STlauncher.App.ViewModels;

/// <summary>The file a pack replaced at an update, while the launcher still keeps it.</summary>
public partial class ResourcePackItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrevious))]
    [NotifyPropertyChangedFor(nameof(PreviousLabel))]
    private ReplacedModFile? _previous;

    public bool HasPrevious => Previous is not null;

    /// <summary>The same words the mod list uses: "Bring back the previous version (1.2)".</summary>
    public string PreviousLabel => MainWindowViewModel.PreviousPackLabel(Previous);

    /// <summary>The row's menu opens in a popup, out of reach of a binding to the page.</summary>
    public System.Windows.Input.ICommand? RestorePreviousCommand { get; set; }
}

public partial class ShaderPackItem
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrevious))]
    [NotifyPropertyChangedFor(nameof(PreviousLabel))]
    private ReplacedModFile? _previous;

    public bool HasPrevious => Previous is not null;

    public string PreviousLabel => MainWindowViewModel.PreviousPackLabel(Previous);

    public System.Windows.Input.ICommand? RestorePreviousCommand { get; set; }
}

/// <summary>
/// "Bring back the previous version" for resource packs and shaders. A pack update goes
/// through the same replacer as a mod update and keeps the old file the same way; this is
/// the way back to it, and like there nothing is downloaded.
/// </summary>
public partial class MainWindowViewModel
{
    internal static string PreviousPackLabel(ReplacedModFile? previous)
        => previous is null
            ? string.Empty
            : Localize(
                "Mods_RestorePrevious",
                "Bring back the previous version ({0})",
                previous.Label is { Length: > 0 } label ? label : previous.FileName);

    /// <summary>Called by the list refresh: says for each pack of the player's own whether there is a file to go back to.</summary>
    private void RestoreKnownPackPrevious()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var replacer = _mods.Replacer;
        var directory = InstanceDirectory;

        foreach (var pack in EnabledResourcePacks.Concat(DisabledResourcePacks).Where(p => !p.IsCatalog))
        {
            pack.Previous = replacer.FindPrevious(directory, CatalogPlacement.ResourcePacksFolder, pack.FileName);
            pack.RestorePreviousCommand = RestorePreviousPackCommand;
        }

        foreach (var shader in ShaderPacks.Where(s => !s.IsCatalog))
        {
            shader.Previous = replacer.FindPrevious(directory, CatalogPlacement.ShaderPacksFolder, shader.FileName);
            shader.RestorePreviousCommand = RestorePreviousPackCommand;
        }
    }

    /// <summary>
    /// The file the last update moved aside returns, and the newer one is kept in its
    /// turn. The pack keeps its place: an enabled resource pack stays enabled at the same
    /// position, the active shader stays the active one.
    /// </summary>
    [RelayCommand]
    private async Task RestorePreviousPackAsync(object? item)
    {
        var pack = item as ResourcePackItem;
        var shader = item as ShaderPackItem;

        var previous = pack?.Previous ?? shader?.Previous;
        var record = pack?.Record ?? shader?.Record;
        var name = pack?.Name ?? shader?.Name ?? string.Empty;
        var fileName = pack?.FileName ?? shader?.FileName ?? string.Empty;

        if (previous is null || SelectedInstance is not { } instance || PacksLocked())
        {
            return;
        }

        var directory = InstanceDirectory;

        // Read before the swap: the lists are rebuilt after it.
        var position = pack is { IsEnabled: true } ? EnabledResourcePacks.IndexOf(pack) : -1;
        var enabledNames = EnabledResourcePacks.Select(p => p.FileName).ToList();
        var wasActive = shader is { IsActive: true };

        try
        {
            await MaybeBackupAsync(BackupTrigger.BeforeModChange, instance);

            var replacer = Replacer;
            var result = await Task.Run(() => replacer.Restore(directory, previous, record?.Version));

            // The record follows the file, as it does for a mod.
            if (record is not null)
            {
                record.FileName = result.NewFileName;
                record.Version = previous.Label;
                _instances.Save(instance);
            }

            if (position >= 0 && !string.Equals(result.NewFileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                enabledNames[position] = result.NewFileName;
                ResourcePackOrder.WriteEnabled(directory, enabledNames);
            }

            if (wasActive && IsSelectedBuild(instance))
            {
                WriteShaderChoice(result.NewFileName);
            }

            AppendConsole($"[packs] {previous.FileName} is back in place of {fileName}; that one is kept the same way");
            _knownPackUpdates.Remove(fileName);

            if (IsSelectedBuild(instance))
            {
                RefreshMods();
            }

            Status = Localize("Mods_RestoredPrevious", "{0}: the previous version is back. The newer file is kept, so this can be undone the same way.", name);
        }
        catch (Exception ex)
        {
            Status = Localize("Mods_RestoreFailed", "{0} stays as it was. {1}", name, DescribeFailure(ex));
            AppendConsole(ex.ToString());
        }
    }
}
