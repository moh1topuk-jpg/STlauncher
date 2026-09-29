using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using STlauncher.Core.Content;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

/// <summary>
/// Files dropped onto the builds page, and the trail a freshly added mod leaves in the
/// list. A mod added from the catalog used to land somewhere in a list of thirty with
/// nothing to tell it apart; now the list opens on it, outlined, and the outline stays
/// long enough to be found.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Files of the current browser install, in the order they arrived.</summary>
    private readonly List<string> _installBatch = new();

    /// <summary>File names outlined in the mod list right now.</summary>
    private readonly HashSet<string> _freshModFiles = new(StringComparer.OrdinalIgnoreCase);

    private DispatcherTimer? _freshModTimer;

    /// <summary>Raised with a file name the builds page should scroll to.</summary>
    public event Action<string>? RevealModRequested;

    /// <summary>True while a file drag hovers over the builds page; the page shows where it will go.</summary>
    [ObservableProperty]
    private bool _isDropHover;

    /// <summary>
    /// Outlines the files and, for the first of a series, opens the list on the mod. A
    /// second mod added while the first is still outlined only joins the outline: the
    /// player is adding several and does not want to be pulled away after each one.
    /// </summary>
    private void RevealFreshMods(IReadOnlyList<string> fileNames, string? focus)
    {
        if (fileNames.Count == 0)
        {
            return;
        }

        var wasQuiet = _freshModFiles.Count == 0;

        foreach (var name in fileNames)
        {
            _freshModFiles.Add(name);
        }

        foreach (var item in InstalledMods)
        {
            item.IsNew = _freshModFiles.Contains(item.FileName);
        }

        OnPropertyChanged(nameof(ModsTabLabel));

        _freshModTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _freshModTimer.Stop();
        _freshModTimer.Tick -= ClearFreshMods;
        _freshModTimer.Tick += ClearFreshMods;
        _freshModTimer.Start();

        if (wasQuiet && focus is not null)
        {
            Section = ShellSection.Builds;
            BuildTab = BuildTab.Mods;
            RevealModRequested?.Invoke(focus);
        }
        else if (fileNames.Count > 0)
        {
            Status = Localize("Mods_AddedMarked", "Added: {0}. Outlined in the mod list.", string.Join(", ", fileNames));
        }
    }

    private void ClearFreshMods(object? sender, EventArgs e)
    {
        _freshModTimer?.Stop();
        _freshModFiles.Clear();

        foreach (var item in InstalledMods)
        {
            item.IsNew = false;
        }

        OnPropertyChanged(nameof(ModsTabLabel));
    }

    // ===================== Dropped files =====================

    /// <summary>
    /// Files dragged onto the page: jars into mods, zips into the folder of the open tab
    /// or, from the mods tab, wherever their contents say, modpacks through the importer.
    /// Copies, never moves: the file the player dragged stays where it was.
    /// </summary>
    public async Task AddLocalFilesAsync(IReadOnlyList<string> paths)
    {
        IsDropHover = false;

        if (SelectedInstance is null || paths.Count == 0)
        {
            return;
        }

        if (IsModsBusy || IsBrowserBusy)
        {
            Status = Localize("Mods_DropBusy", "Wait for the current install to finish, then drop again");
            return;
        }

        var added = new List<(string Folder, string FileName)>();
        var skipped = new List<string>();
        var modpacks = new List<string>();

        foreach (var path in paths)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();

            if (extension == ".mrpack")
            {
                modpacks.Add(path);
                continue;
            }

            var folder = extension switch
            {
                ".jar" => ModManager.ModsFolderName,
                ".zip" => ZipFolder(path),
                _ => null
            };

            if (folder is null || !File.Exists(path))
            {
                skipped.Add(Path.GetFileName(path));
                continue;
            }

            added.Add((folder, Path.GetFileName(path)));
        }

        try
        {
            if (added.Count > 0)
            {
                await MaybeBackupAsync(BackupTrigger.BeforeModChange);
            }

            var directory = InstanceDirectory;
            var done = new List<(string Folder, string FileName)>();

            foreach (var (folder, fileName) in added)
            {
                var source = paths.First(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));
                var target = Path.Combine(directory, folder, fileName);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length &&
                        string.Equals(ModManager.TryComputeSha1(target), ModManager.TryComputeSha1(source), StringComparison.OrdinalIgnoreCase))
                    {
                        AppendConsole($"[drop] {folder}/{fileName}: already in the build, same file");
                        done.Add((folder, fileName));
                        continue;
                    }

                    File.Copy(source, target, overwrite: true);
                    AppendConsole($"[drop] copied {folder}/{fileName} from {source}");

                    if (folder == ModManager.ModsFolderName)
                    {
                        ReplaceOtherVersions(SelectedInstance, fileName);
                    }

                    RecordInstalledMod(new InstalledModRecord
                    {
                        FileName = fileName,
                        Source = ModSource.Manual,
                        Name = Path.GetFileNameWithoutExtension(fileName),
                        Folder = folder
                    });

                    done.Add((folder, fileName));
                }
                catch (Exception ex)
                {
                    AppendConsole($"[drop] {fileName}: {ex.Message}");
                    skipped.Add(fileName);
                }
            }

            RefreshMods();

            foreach (var pack in modpacks)
            {
                await ImportModpackAsync(pack);
            }

            var mods = done.Where(d => d.Folder == ModManager.ModsFolderName).Select(d => d.FileName).ToList();
            var packsFolder = done.Select(d => d.Folder).FirstOrDefault(f => f != ModManager.ModsFolderName);

            if (mods.Count > 0)
            {
                Section = ShellSection.Builds;
                BuildTab = BuildTab.Mods;
                RevealFreshMods(mods, mods[0]);
            }
            else if (packsFolder is not null)
            {
                BuildTab = packsFolder == CatalogPlacement.ShaderPacksFolder ? BuildTab.Shaders : BuildTab.ResourcePacks;
            }

            Status = skipped.Count == 0
                ? Localize("Mods_DropDone", "Added to the build: {0}", done.Count + modpacks.Count)
                : Localize("Mods_DropPartial", "Added: {0}. Not a mod or pack: {1}", done.Count + modpacks.Count, string.Join(", ", skipped));
        }
        catch (Exception ex)
        {
            Status = Localize("Mods_DropFailed", "Could not add the files: {0}", ex.Message);
            AppendConsole($"[drop] failed: {ex}");
        }
    }

    /// <summary>A zip is a shader pack when it carries a shaders folder, a resource pack otherwise; the open tab overrides.</summary>
    private string ZipFolder(string path)
    {
        if (BuildTab == BuildTab.Shaders)
        {
            return CatalogPlacement.ShaderPacksFolder;
        }

        if (BuildTab == BuildTab.ResourcePacks)
        {
            return CatalogPlacement.ResourcePacksFolder;
        }

        try
        {
            using var zip = ZipFile.OpenRead(path);

            if (zip.Entries.Any(e => e.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)))
            {
                return CatalogPlacement.ShaderPacksFolder;
            }
        }
        catch (Exception)
        {
        }

        return CatalogPlacement.ResourcePacksFolder;
    }
}
