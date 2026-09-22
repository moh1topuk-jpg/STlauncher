using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;

namespace STlauncher.App.ViewModels;

/// <summary>
/// "The build changed" on the main screen. The server owner adds a mod to the catalog
/// and every launcher installs it at the next start; without this the player only ever
/// noticed by the download counter. The lines stay until dismissed, across restarts.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<string> BuildChangeLines { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildChangeNotice))]
    private string _buildChangeTitle = string.Empty;

    public bool HasBuildChangeNotice => BuildChangeLines.Count > 0;

    /// <summary>Restores the lines saved by a previous session that nobody dismissed.</summary>
    private void LoadBuildChangeNotice(IReadOnlyList<string> lines, string? title)
    {
        BuildChangeLines.Clear();

        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            BuildChangeLines.Add(line);
        }

        BuildChangeTitle = BuildChangeLines.Count == 0
            ? string.Empty
            : title ?? Localize("BuildChange_Title", "Build updated");

        OnPropertyChanged(nameof(HasBuildChangeNotice));
    }

    /// <summary>Words a diff of the catalog-owned parts of the build and puts it on screen.</summary>
    private void AnnounceBuildChanges(string buildName, BuildChangeNotice notice)
    {
        if (!notice.Any)
        {
            return;
        }

        var lines = new List<string>();

        if (notice.VersionChanged)
        {
            lines.Add(Localize("BuildChange_Version", "Minecraft {0} → {1}", notice.OldVersion, notice.NewVersion));
        }

        if (notice.LoaderChanged)
        {
            lines.Add(Localize("BuildChange_Loader", "{0} → {1}", notice.OldLoader, notice.NewLoader));
        }

        if (notice.Added.Count > 0)
        {
            lines.Add(Localize("BuildChange_Added", "Added: {0}", string.Join(", ", notice.Added)));
        }

        if (notice.Removed.Count > 0)
        {
            lines.Add(Localize("BuildChange_Removed", "Removed: {0}", string.Join(", ", notice.Removed)));
        }

        AppendBuildChangeLines(buildName, lines);
    }

    /// <summary>Pinned versions bumped by the catalog, reported by the sync as it installs them.</summary>
    private void AnnounceBuildUpdates(string buildName, IReadOnlyList<string> updatedNames)
    {
        if (updatedNames.Count > 0)
        {
            AppendBuildChangeLines(buildName, new[]
            {
                Localize("BuildChange_Updated", "Updated: {0}", string.Join(", ", updatedNames))
            });
        }
    }

    private void AppendBuildChangeLines(string buildName, IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Where(l => !BuildChangeLines.Contains(l)))
        {
            BuildChangeLines.Add(line);
        }

        BuildChangeTitle = Localize("BuildChange_TitleFor", "\"{0}\" updated from the catalog", buildName);
        OnPropertyChanged(nameof(HasBuildChangeNotice));
        PersistSettings();
    }

    [RelayCommand]
    private void DismissBuildChangeNotice()
    {
        BuildChangeLines.Clear();
        BuildChangeTitle = string.Empty;
        OnPropertyChanged(nameof(HasBuildChangeNotice));
        PersistSettings();
    }

    /// <summary>A catalog item's display name, falling back to its id.</summary>
    private string CatalogItemName(string id) => _loadedCatalog?.FindItem(id)?.Name is { Length: > 0 } name ? name : id;
}
