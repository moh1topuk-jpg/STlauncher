using System;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Instances;

namespace STlauncher.App.ViewModels;

// What the rows of the builds page need beyond what the lists already knew: the one
// muted line under a mod's name, the address of its page, and "show the file".

public partial class InstalledModItem
{
    /// <summary>
    /// The line under the name. A switched-off mod says so, and says whose doing it was
    /// when the launcher knows; otherwise where the file came from and what it weighs.
    /// </summary>
    public string SubLine
    {
        get
        {
            var size = Converters.FileSizeConverter.Instance.Convert(Size, typeof(string), null, System.Globalization.CultureInfo.CurrentCulture) as string ?? string.Empty;

            if (!Enabled)
            {
                return Record?.DisabledByUser == true
                    ? MainWindowViewModel.Localize("Mods_OffByYou", "Switched off by you")
                    : MainWindowViewModel.Localize("Mods_Off", "Switched off");
            }

            var origin = Record?.Source switch
            {
                ModSource.Catalog => MainWindowViewModel.Localize("Packs_SourceCatalog", "server catalog"),
                ModSource.Modrinth => "Modrinth",
                ModSource.CurseForge => "CurseForge",
                ModSource.Modpack => MainWindowViewModel.Localize("Packs_SourceModpack", "from a modpack"),
                _ => MainWindowViewModel.Localize("Packs_SourceManual", "added by hand")
            };

            return origin + " · " + size;
        }
    }

    /// <summary>The mod's page at its source, when the launcher knows which project the file is.</summary>
    public string? PageUrl => Record switch
    {
        { Source: ModSource.CurseForge, ProjectId.Length: > 0 } => "https://www.curseforge.com/projects/" + Record.ProjectId,
        { Source: ModSource.Modrinth or ModSource.Catalog, Id.Length: > 0 } => "https://modrinth.com/mod/" + Record.Id,
        _ => ProjectId is { Length: > 0 } ? "https://modrinth.com/mod/" + ProjectId : null
    };

    public bool HasPage => PageUrl is not null;
}

public partial class MainWindowViewModel
{
    /// <summary>Opens the folder with the file selected: a mod, a resource pack or a shader.</summary>
    [RelayCommand]
    private void ShowContentFile(object? item)
    {
        var path = item switch
        {
            InstalledModItem mod => mod.Path,
            ResourcePackItem pack => pack.Path,
            ShaderPackItem shader => shader.Path,
            _ => null
        };

        if (!string.IsNullOrEmpty(path))
        {
            RevealInFileManager(path);
        }
    }

    [RelayCommand]
    private void OpenModPage(InstalledModItem? item)
    {
        if (item?.PageUrl is { } url)
        {
            OpenUrl(url);
        }
    }

    /// <summary>The "boost is on" chip leads to where the boost is switched: the settings page.</summary>
    [RelayCommand]
    private void OpenBoostSettings() => Section = ShellSection.Settings;
}
