using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Content;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;
using STlauncher.Core.Packs;

namespace STlauncher.App.ViewModels;

/// <summary>
/// A resource pack in the build: what its pack.mcmeta says, whether the game will use it,
/// and where it stands in the order. The list on screen is the same list the game's own
/// menu shows, so "top wins" holds here too.
/// </summary>
public partial class ResourcePackItem : ObservableObject
{
    public ResourcePackItem(ResourcePackInfo info, InstalledModRecord? record, int? gameFormat, string? gameVersion)
    {
        Info = info;
        Record = record;
        IsCatalog = record?.Source == ModSource.Catalog;

        FormatLabel = info.Format is null && info.MinFormat is null
            ? MainWindowViewModel.Localize("Packs_FormatUnknown", "not declared")
            : info.MinFormat is { } min && info.MaxFormat is { } max && min != max
                ? $"{min}–{max}"
                : (info.Format ?? info.MinFormat)?.ToString() ?? string.Empty;

        if (gameFormat is null || (info.Format is null && info.MinFormat is null))
        {
            Fits = null;
            CompatibilityLabel = string.Empty;
        }
        else if (info.Supports(gameFormat.Value))
        {
            Fits = true;
            CompatibilityLabel = MainWindowViewModel.Localize("Packs_Fits", "fits {0}", gameVersion ?? string.Empty);
        }
        else
        {
            Fits = false;
            CompatibilityLabel = MainWindowViewModel.Localize("Packs_Mismatch", "made for another version — may look off");
        }

        try
        {
            if (info.Icon is { Length: > 0 })
            {
                using var stream = new MemoryStream(info.Icon);
                Preview = new Bitmap(stream);
            }
        }
        catch (Exception)
        {
            // A broken pack.png is not worth a broken row.
        }
    }

    public ResourcePackInfo Info { get; }

    public InstalledModRecord? Record { get; }

    public string Name => Record?.Name is { Length: > 0 } name ? name : Info.Name;

    public string FileName => Info.FileName;

    public string Path => Info.Path;

    public long Size => Info.Size;

    public string Description => Info.Description;

    public bool HasDescription => Info.Description.Length > 0;

    /// <summary>"46" or "34–46", or a "not declared" note.</summary>
    public string FormatLabel { get; }

    /// <summary>Null when either side is unknown; otherwise whether the game accepts this pack.</summary>
    public bool? Fits { get; }

    public bool Mismatch => Fits == false;

    public bool IsFit => Fits == true;

    public string CompatibilityLabel { get; }

    public bool HasCompatibilityLabel => CompatibilityLabel.Length > 0;

    public Bitmap? Preview { get; }

    public bool HasPreview => Preview is not null;

    public bool IsCatalog { get; }

    public string? ModrinthSlug => Record?.Source is ModSource.Modrinth or ModSource.Catalog ? Record.Id : null;

    public bool HasModrinthPage => !string.IsNullOrEmpty(ModrinthSlug);

    public string SourceLabel => Record?.Source switch
    {
        ModSource.Catalog => MainWindowViewModel.Localize("Packs_SourceCatalog", "server catalog"),
        ModSource.Modrinth => MainWindowViewModel.Localize("Packs_SourceModrinth", "Modrinth") + VersionSuffix,
        ModSource.Modpack => MainWindowViewModel.Localize("Packs_SourceModpack", "from a modpack"),
        _ => MainWindowViewModel.Localize("Packs_SourceManual", "added by hand")
    };

    private string VersionSuffix => Record?.Version is { Length: > 0 } v ? " · " + v : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    private bool _isEnabled;

    /// <summary>1-based place among the enabled packs; 0 when off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    private int _position;

    [ObservableProperty]
    private bool _isFirst;

    [ObservableProperty]
    private bool _isLast;

    [ObservableProperty]
    private bool _isSelected;

    public string StateLabel => IsEnabled
        ? MainWindowViewModel.Localize("Packs_StateOn", "enabled · #{0}", Position)
        : MainWindowViewModel.Localize("Packs_StateOff", "disabled");
}

/// <summary>A shader pack in shaderpacks/. Iris loads exactly one of them, or none.</summary>
public partial class ShaderPackItem : ObservableObject
{
    public ShaderPackItem(InstalledMod file, InstalledModRecord? record)
    {
        File = file;
        Record = record;
        IsCatalog = record?.Source == ModSource.Catalog;
    }

    public InstalledMod File { get; }

    public InstalledModRecord? Record { get; }

    public string Name => Record?.Name is { Length: > 0 } name ? name : File.DisplayName;

    public string FileName => File.FileName;

    public string Path => File.Path;

    public long Size => File.Size;

    public bool IsCatalog { get; }

    public string? ModrinthSlug => Record?.Source is ModSource.Modrinth or ModSource.Catalog ? Record.Id : null;

    public bool HasModrinthPage => !string.IsNullOrEmpty(ModrinthSlug);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotActive))]
    private bool _isActive;

    public bool IsNotActive => !IsActive;
}

/// <summary>
/// Resource packs and shaders of the build, on their own tabs. The launcher writes what
/// the game reads at start: the pack order in options.txt and the shader in Iris's
/// properties. Nothing here is touched while the game runs, because the game would write
/// its own copy over it on exit.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<ResourcePackItem> EnabledResourcePacks { get; } = new();

    public ObservableCollection<ResourcePackItem> DisabledResourcePacks { get; } = new();

    public ObservableCollection<ShaderPackItem> ShaderPacks { get; } = new();

    [ObservableProperty]
    private ResourcePackItem? _selectedResourcePack;

    [ObservableProperty]
    private int _resourcePackCount;

    [ObservableProperty]
    private int _shaderPackCount;

    /// <summary>Iris or Oculus is in the build and switched on; without one, shaders do nothing.</summary>
    [ObservableProperty]
    private bool _hasShaderLoader;

    /// <summary>The loaders found among the enabled jars; the active pack is written to each.</summary>
    private IReadOnlyList<ShaderLoader> _shaderLoaders = Array.Empty<ShaderLoader>();

    /// <summary>"Iris" on Fabric, Quilt and NeoForge; "Oculus" on Forge. What the build should have.</summary>
    public string ExpectedShaderLoader => SelectedLoader == Core.Loaders.LoaderKind.Forge ? "Oculus" : "Iris";

    public string ShaderLoaderMissingText => Localize("Shaders_NeedLoader", "Shaders need the {0} mod — this build has none, or it is switched off.", ExpectedShaderLoader);

    public string FindShaderLoaderLabel => Localize("Shaders_FindLoader", "Find {0} in the catalog", ExpectedShaderLoader);

    /// <summary>Name of the shader Iris will load, or empty when shaders are off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoShaderActive))]
    private string _activeShaderName = string.Empty;

    public bool NoShaderActive => ActiveShaderName.Length == 0;

    public bool HasNoResourcePacks => ResourcePackCount == 0;

    public bool HasNoShaderPacks => ShaderPackCount == 0;

    public bool HasNoSelectedResourcePack => SelectedResourcePack is null;

    public string EnabledPacksHeader => Localize("Packs_EnabledHeader", "Enabled · {0}", EnabledResourcePacks.Count);

    public string DisabledPacksHeader => Localize("Packs_DisabledHeader", "Disabled · {0}", DisabledResourcePacks.Count);

    public bool HasDisabledResourcePacks => DisabledResourcePacks.Count > 0;

    /// <summary>Pack edits are file edits the game would overwrite on exit, so they wait.</summary>
    public bool CanEditPacks => !IsGameRunning;

    public string ModsTabLabel => TabLabel("Builds_TabMods", "Mods", InstalledMods.Count);

    public string ResourcePacksTabLabel => TabLabel("Builds_TabPacks", "Resource packs", ResourcePackCount);

    public string ShadersTabLabel => TabLabel("Builds_TabShaders", "Shaders", ShaderPackCount);

    private static string TabLabel(string key, string fallback, int count)
        => count > 0 ? Localize(key, fallback) + " · " + count : Localize(key, fallback);

    private int? _packFormat;
    private string? _packFormatVersion;

    partial void OnSelectedResourcePackChanged(ResourcePackItem? oldValue, ResourcePackItem? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        OnPropertyChanged(nameof(HasNoSelectedResourcePack));
    }

    /// <summary>
    /// Rebuilds both lists from disk and options.txt. Called from RefreshMods, so every
    /// path that changes files - install, delete, import, restore - refreshes packs too.
    /// </summary>
    private void RefreshPacks()
    {
        try
        {
            var selected = SelectedResourcePack?.FileName;
            var gameFormat = ResolvePackFormat();
            var packs = ResourcePackInspector.ListPacks(System.IO.Path.Combine(InstanceDirectory, CatalogPlacement.ResourcePacksFolder));
            var order = ResourcePackOrder.ReadEnabled(InstanceDirectory);

            var items = packs
                .Select(p => new ResourcePackItem(p, FindRecord(p.FileName, CatalogPlacement.ResourcePacksFolder), gameFormat, SelectedInstance?.VersionId))
                .ToList();

            var byName = items.ToDictionary(i => i.FileName, StringComparer.OrdinalIgnoreCase);

            EnabledResourcePacks.Clear();
            DisabledResourcePacks.Clear();

            foreach (var name in order)
            {
                if (byName.Remove(name, out var item))
                {
                    item.IsEnabled = true;
                    EnabledResourcePacks.Add(item);
                }
            }

            foreach (var item in items.Where(i => !i.IsEnabled))
            {
                DisabledResourcePacks.Add(item);
            }

            RenumberResourcePacks();
            ResourcePackCount = items.Count;

            SelectedResourcePack = items.FirstOrDefault(i => string.Equals(i.FileName, selected, StringComparison.OrdinalIgnoreCase))
                                   ?? EnabledResourcePacks.FirstOrDefault()
                                   ?? DisabledResourcePacks.FirstOrDefault();

            RefreshShaders();
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Packs", "Failed to read the packs: {0}", ex.Message);
        }

        OnPropertyChanged(nameof(HasNoResourcePacks));
        OnPropertyChanged(nameof(HasNoShaderPacks));
        OnPropertyChanged(nameof(EnabledPacksHeader));
        OnPropertyChanged(nameof(DisabledPacksHeader));
        OnPropertyChanged(nameof(HasDisabledResourcePacks));
        OnPropertyChanged(nameof(ModsTabLabel));
        OnPropertyChanged(nameof(ResourcePacksTabLabel));
        OnPropertyChanged(nameof(ShadersTabLabel));
    }

    private void RefreshShaders()
    {
        _shaderLoaders = ShaderConfig.Detect(InstalledMods.Where(m => m.IsMod && m.Enabled).Select(m => m.FileName));
        HasShaderLoader = _shaderLoaders.Count > 0;

        // With no loader in the build the Iris file still tells what the player picked.
        var settings = ShaderConfig.Read(InstanceDirectory, _shaderLoaders.FirstOrDefault());
        var active = settings.Enabled ? settings.ShaderPack ?? string.Empty : string.Empty;

        ShaderPacks.Clear();

        foreach (var file in _mods.ListPacks(InstanceDirectory, CatalogPlacement.ShaderPacksFolder))
        {
            ShaderPacks.Add(new ShaderPackItem(file, FindRecord(file.FileName, CatalogPlacement.ShaderPacksFolder))
            {
                IsActive = string.Equals(file.FileName, active, StringComparison.OrdinalIgnoreCase)
            });
        }

        ShaderPackCount = ShaderPacks.Count;
        ActiveShaderName = ShaderPacks.FirstOrDefault(s => s.IsActive)?.Name ?? string.Empty;
        OnPropertyChanged(nameof(ExpectedShaderLoader));
        OnPropertyChanged(nameof(ShaderLoaderMissingText));
        OnPropertyChanged(nameof(FindShaderLoaderLabel));
    }

    /// <summary>Every loader in the build gets the choice; Iris's file when there is none yet.</summary>
    private void WriteShaderChoice(string? shaderPack)
    {
        foreach (var loader in _shaderLoaders.Count > 0 ? _shaderLoaders : new[] { ShaderLoader.Iris })
        {
            ShaderConfig.Write(InstanceDirectory, shaderPack, loader);
        }
    }

    private InstalledModRecord? FindRecord(string fileName, string folder)
        => SelectedInstance?.InstalledMods.FirstOrDefault(m =>
            string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.Folder ?? ModManager.ModsFolderName, folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>The format the selected game version wants, read once per version from its jar.</summary>
    private int? ResolvePackFormat()
    {
        var version = SelectedInstance?.VersionId;

        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        if (_packFormatVersion != version)
        {
            _packFormatVersion = version;
            _packFormat = GamePackFormat.ReadResourceFormat(_paths.VersionJarPath(version));
        }

        return _packFormat;
    }

    private void RenumberResourcePacks()
    {
        for (var i = 0; i < EnabledResourcePacks.Count; i++)
        {
            var item = EnabledResourcePacks[i];
            item.Position = i + 1;
            item.IsFirst = i == 0;
            item.IsLast = i == EnabledResourcePacks.Count - 1;
        }

        foreach (var item in DisabledResourcePacks)
        {
            item.Position = 0;
            item.IsFirst = item.IsLast = false;
        }
    }

    private void SaveResourcePackOrder()
    {
        ResourcePackOrder.WriteEnabled(InstanceDirectory, EnabledResourcePacks.Select(p => p.FileName).ToList());
        RenumberResourcePacks();
        OnPropertyChanged(nameof(EnabledPacksHeader));
        OnPropertyChanged(nameof(DisabledPacksHeader));
        OnPropertyChanged(nameof(HasDisabledResourcePacks));
    }

    private bool PacksLocked()
    {
        if (!CanEditPacks)
        {
            Status = Localize("Packs_Locked", "Packs can be changed once the game is closed");
            return true;
        }

        return false;
    }

    // ===================== Resource pack commands =====================

    [RelayCommand]
    private void SelectResourcePack(ResourcePackItem? item) => SelectedResourcePack = item;

    [RelayCommand]
    private void ToggleResourcePack(ResourcePackItem? item)
    {
        if (item is null || PacksLocked())
        {
            return;
        }

        try
        {
            if (item.IsEnabled)
            {
                EnabledResourcePacks.Remove(item);
                item.IsEnabled = false;
                DisabledResourcePacks.Insert(0, item);
            }
            else
            {
                DisabledResourcePacks.Remove(item);
                item.IsEnabled = true;
                // A pack switched on goes on top: that is what the player wants to see.
                EnabledResourcePacks.Insert(0, item);
            }

            SaveResourcePackOrder();
            SelectedResourcePack = item;
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Packs", "Failed to change the packs: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void MoveResourcePackUp(ResourcePackItem? item) => MoveResourcePack(item, -1);

    [RelayCommand]
    private void MoveResourcePackDown(ResourcePackItem? item) => MoveResourcePack(item, +1);

    private void MoveResourcePack(ResourcePackItem? item, int delta)
    {
        if (item is null || !item.IsEnabled || PacksLocked())
        {
            return;
        }

        var index = EnabledResourcePacks.IndexOf(item);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= EnabledResourcePacks.Count)
        {
            return;
        }

        try
        {
            EnabledResourcePacks.Move(index, target);
            SaveResourcePackOrder();
            SelectedResourcePack = item;
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Packs", "Failed to change the packs: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteResourcePackAsync(ResourcePackItem? item)
    {
        if (item is null || PacksLocked())
        {
            return;
        }

        try
        {
            await MaybeBackupAsync(BackupTrigger.BeforeModChange);
            _mods.Uninstall(item.Path);
            ForgetRecord(item.FileName);

            if (item.IsEnabled)
            {
                EnabledResourcePacks.Remove(item);
                SaveResourcePackOrder();
            }

            RefreshMods();
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Packs", "Failed to change the packs: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenResourcePackOnModrinth(ResourcePackItem? item)
    {
        if (item?.ModrinthSlug is { Length: > 0 } slug)
        {
            OpenUrl($"https://modrinth.com/resourcepack/{slug}");
        }
    }

    [RelayCommand]
    private void OpenResourcePacksFolder() => OpenInstanceFolder(CatalogPlacement.ResourcePacksFolder);

    /// <summary>The catalog tab, already switched to resource packs.</summary>
    [RelayCommand]
    private void AddResourcePacks()
    {
        SelectBrowserKind(ProjectTypes.ResourcePack);
        BuildTab = BuildTab.Catalog;
    }

    // ===================== Shader commands =====================

    [RelayCommand]
    private void ActivateShader(ShaderPackItem? item)
    {
        if (item is null || PacksLocked())
        {
            return;
        }

        try
        {
            WriteShaderChoice(item.FileName);
            RefreshShaders();
            OnPropertyChanged(nameof(HasNoShaderPacks));
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Shaders", "Failed to change the shader: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void DisableShaders()
    {
        if (PacksLocked())
        {
            return;
        }

        try
        {
            WriteShaderChoice(null);
            RefreshShaders();
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Shaders", "Failed to change the shader: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteShaderAsync(ShaderPackItem? item)
    {
        if (item is null || PacksLocked())
        {
            return;
        }

        try
        {
            await MaybeBackupAsync(BackupTrigger.BeforeModChange);
            _mods.Uninstall(item.Path);
            ForgetRecord(item.FileName);

            if (item.IsActive)
            {
                WriteShaderChoice(null);
            }

            RefreshMods();
        }
        catch (Exception ex)
        {
            Status = Localize("Error_Shaders", "Failed to change the shader: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenShaderOnModrinth(ShaderPackItem? item)
    {
        if (item?.ModrinthSlug is { Length: > 0 } slug)
        {
            OpenUrl($"https://modrinth.com/shader/{slug}");
        }
    }

    [RelayCommand]
    private void OpenShaderPacksFolder() => OpenInstanceFolder(CatalogPlacement.ShaderPacksFolder);

    [RelayCommand]
    private void AddShaders()
    {
        SelectBrowserKind(ProjectTypes.Shader);
        BuildTab = BuildTab.Catalog;
    }

    /// <summary>The shader loader is a mod, so the search happens on the mods side of the catalog.</summary>
    [RelayCommand]
    private async Task FindShaderLoaderAsync()
    {
        SelectBrowserKind(ProjectTypes.Mod);
        ModSearchQuery = ExpectedShaderLoader.ToLowerInvariant();
        BuildTab = BuildTab.Catalog;
        await SearchModsAsync();
    }

    private void ForgetRecord(string fileName)
    {
        if (SelectedInstance is null)
        {
            return;
        }

        SelectedInstance.InstalledMods.RemoveAll(m =>
            string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        _instances.Save(SelectedInstance);
    }

    private void OpenInstanceFolder(string folder)
    {
        try
        {
            var path = System.IO.Path.Combine(InstanceDirectory, folder);
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = Localize("Error_OpenFolder", "Failed to open the folder: {0}", ex.Message);
        }
    }
}
