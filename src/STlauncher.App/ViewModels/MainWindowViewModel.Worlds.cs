using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Instances;
using STlauncher.Core.Worlds;

namespace STlauncher.App.ViewModels;

/// <summary>One backup of one world, as a row under its card.</summary>
public partial class WorldBackupItem : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private readonly WorldItem _world;

    public WorldBackupItem(MainWindowViewModel owner, WorldItem world, WorldBackupInfo info)
    {
        _owner = owner;
        _world = world;
        Info = info;
        DateLabel = info.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        SizeLabel = MainWindowViewModel.WorldSizeLabel(info.Size);
    }

    public WorldBackupInfo Info { get; }

    public string DateLabel { get; }

    public string SizeLabel { get; }

    [RelayCommand]
    private Task RestoreAsync() => _owner.RestoreWorldBackupAsync(_world, this);
}

/// <summary>A deleted world in the launcher's trash: what it was, and until when it can come back.</summary>
public partial class TrashedWorldItem : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public TrashedWorldItem(MainWindowViewModel owner, TrashedWorld info)
    {
        _owner = owner;
        Info = info;
        DetailLabel = MainWindowViewModel.Localize(
            "Worlds_TrashDetail",
            "Deleted {0:dd.MM.yyyy HH:mm} · kept until {1:dd.MM.yyyy}",
            info.DeletedAt.ToLocalTime(),
            info.ExpiresAt.ToLocalTime());
    }

    public TrashedWorld Info { get; }

    public string Name => Info.Name;

    public string DetailLabel { get; }

    [RelayCommand]
    private Task RestoreAsync() => _owner.RestoreTrashedWorldAsync(this);
}

/// <summary>
/// A world's card. What level.dat says is here from the start; the icon, the size on
/// disk and the backups arrive afterwards from a background pass, so a build with fifty
/// worlds shows its list at once.
/// </summary>
public partial class WorldItem : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public WorldItem(MainWindowViewModel owner, WorldInfo info)
    {
        _owner = owner;
        Info = info;
        _name = info.Name;

        var facts = new List<string>();

        if (info.IsReadable)
        {
            // Hardcore is how the player thinks of the mode, though the file says "survival" and a flag.
            facts.Add(info.IsHardcore
                ? MainWindowViewModel.Localize("Worlds_ModeHardcore", "Hardcore")
                : info.GameMode switch
                {
                    WorldGameMode.Creative => MainWindowViewModel.Localize("Worlds_ModeCreative", "Creative"),
                    WorldGameMode.Adventure => MainWindowViewModel.Localize("Worlds_ModeAdventure", "Adventure"),
                    WorldGameMode.Spectator => MainWindowViewModel.Localize("Worlds_ModeSpectator", "Spectator"),
                    _ => MainWindowViewModel.Localize("Worlds_ModeSurvival", "Survival")
                });

            if (info.Difficulty is { } difficulty)
            {
                facts.Add(difficulty switch
                {
                    WorldDifficulty.Peaceful => MainWindowViewModel.Localize("Worlds_DifficultyPeaceful", "Peaceful"),
                    WorldDifficulty.Easy => MainWindowViewModel.Localize("Worlds_DifficultyEasy", "Easy"),
                    WorldDifficulty.Hard => MainWindowViewModel.Localize("Worlds_DifficultyHard", "Hard"),
                    _ => MainWindowViewModel.Localize("Worlds_DifficultyNormal", "Normal")
                });
            }

            if (info.AllowCheats == true)
            {
                facts.Add(MainWindowViewModel.Localize("Worlds_Cheats", "cheats on"));
            }
        }

        FactsLabel = string.Join(" · ", facts);
        VersionLabel = info.GameVersion ?? string.Empty;
        // For an unreadable world the date is only when the file was written, not when it was played.
        PlayedLabel = info.IsReadable && info.LastPlayed is { } at
            ? MainWindowViewModel.Localize("Worlds_Played", "Played {0}", MainWindowViewModel.WorldTimeLabel(at))
            : string.Empty;
        SeedLabel = info.Seed is { } seed ? seed.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    public WorldInfo Info { get; }

    /// <summary>LevelName: what the game shows in its own list.</summary>
    [ObservableProperty]
    private string _name;

    public string FolderName => Info.FolderName;

    /// <summary>The folder is shown only when it differs from the name, which is when it helps to find the world on disk.</summary>
    public bool ShowFolderName => !string.Equals(Info.FolderName, Info.Name, StringComparison.Ordinal);

    public bool IsUnreadable => !Info.IsReadable;

    public bool IsReadable => Info.IsReadable;

    public bool IsHardcore => Info.IsHardcore;

    /// <summary>"Survival · Hard · cheats on".</summary>
    public string FactsLabel { get; }

    public bool HasFacts => FactsLabel.Length > 0;

    public string VersionLabel { get; }

    public bool HasVersion => VersionLabel.Length > 0;

    public string PlayedLabel { get; }

    public string SeedLabel { get; }

    public bool HasSeed => SeedLabel.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Bitmap? _icon;

    public bool HasIcon => Icon is not null;

    /// <summary>Null until the folder has been measured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeLabel))]
    private long? _sizeBytes;

    public string SizeLabel => SizeBytes is { } bytes ? MainWindowViewModel.WorldSizeLabel(bytes) : "…";

    public ObservableCollection<WorldBackupItem> Backups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackups))]
    [NotifyPropertyChangedFor(nameof(BackupsLabel))]
    private int _backupCount;

    public bool HasBackups => BackupCount > 0;

    public string BackupsLabel => MainWindowViewModel.Localize("Worlds_BackupsCount", "Backups: {0}", BackupCount);

    /// <summary>The backups list is folded away until asked for; most visits are not about restoring.</summary>
    [ObservableProperty]
    private bool _isBackupsOpen;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _nameEdit = string.Empty;

    /// <summary>Delete asks once, on the card itself.</summary>
    [ObservableProperty]
    private bool _isConfirmingDelete;

    /// <summary>True while something is being done to this world: its buttons wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isWorking;

    public bool IsIdle => !IsWorking;

    [RelayCommand]
    private void BeginRename()
    {
        NameEdit = Name;
        IsConfirmingDelete = false;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private Task CommitRenameAsync() => _owner.RenameWorldAsync(this);

    [RelayCommand]
    private Task DuplicateAsync() => _owner.DuplicateWorldAsync(this);

    [RelayCommand]
    private Task BackupAsync() => _owner.BackupWorldAsync(this);

    [RelayCommand]
    private void ToggleBackups() => IsBackupsOpen = !IsBackupsOpen;

    [RelayCommand]
    private void AskDelete()
    {
        IsRenaming = false;
        _owner.AskDeleteWorld(this);
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private Task ConfirmDeleteAsync() => _owner.DeleteWorldAsync(this);

    [RelayCommand]
    private Task CopySeedAsync() => _owner.CopyWorldSeedAsync(this);

    [RelayCommand]
    private void OpenFolder() => _owner.OpenWorldFolder(this);
}

/// <summary>
/// The build's single-player worlds: cards with what level.dat says, and the things a
/// player does to a world. Every action runs on the player's click and off the UI thread;
/// none of them overwrites a world or deletes one for good.
/// </summary>
public partial class MainWindowViewModel
{
    private WorldManager? _worldManager;

    /// <summary>
    /// The one question worlds ask about running games. Today the launcher runs one game
    /// at a time, so any running game counts; when running games are tracked per build,
    /// this is the line that changes.
    /// </summary>
    private bool IsGameRunningIn(string gameDirectory) => IsGameRunning || Core.Launch.RunningGames.IsRunning(gameDirectory);

    private WorldManager WorldsService => _worldManager ??= new WorldManager(IsGameRunningIn);

    public ObservableCollection<WorldItem> Worlds { get; } = new();

    public ObservableCollection<TrashedWorldItem> TrashedWorlds { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoWorlds))]
    [NotifyPropertyChangedFor(nameof(HasWorlds))]
    [NotifyPropertyChangedFor(nameof(WorldsTabLabel))]
    private int _worldCount;

    /// <summary>True from opening the tab until the list is in; the empty state must not flash meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoWorlds))]
    private bool _isWorldsLoading;

    public bool HasNoWorlds => WorldCount == 0 && !IsWorldsLoading;

    public bool HasWorlds => WorldCount > 0;

    public string WorldsTabLabel => TabLabel("Builds_TabWorlds", "Worlds", WorldCount);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrashedWorlds))]
    [NotifyPropertyChangedFor(nameof(TrashLabel))]
    private int _trashedWorldCount;

    public bool HasTrashedWorlds => TrashedWorldCount > 0;

    public string TrashLabel => Localize("Worlds_Trash", "Deleted worlds: {0}", TrashedWorldCount);

    [ObservableProperty]
    private bool _isWorldsTrashOpen;

    /// <summary>What the last action did, or why it did not; shown above the cards.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorldsStatus))]
    private string _worldsStatus = string.Empty;

    public bool HasWorldsStatus => WorldsStatus.Length > 0;

    /// <summary>One action at a time: two zips of two large worlds at once help nobody.</summary>
    [ObservableProperty]
    private bool _isWorldsBusy;

    /// <summary>False while the game runs: it owns the saves folder until it exits.</summary>
    public bool CanEditWorlds => !IsGameRunning;

    private string WorldBackupsDirectory(Instance instance)
        => WorldManager.BackupsDirectoryFor(BackupsDirectory, instance.Id);

    internal static string WorldSizeLabel(long bytes)
        => bytes >= 1024L * 1024 * 1024
            ? Localize("Size_Gb", "{0:F1} GB", bytes / 1024d / 1024 / 1024)
            : bytes >= 1024L * 1024
                ? Localize("Size_Mb", "{0:F0} MB", bytes / 1024d / 1024)
                : Localize("Size_Kb", "{0} KB", Math.Max(1, bytes / 1024));

    internal static string WorldTimeLabel(DateTimeOffset at) => FriendlyTime(at);

    /// <summary>
    /// The number on the tab, for a build that was just selected: folders with a
    /// level.dat, counted without opening any of them.
    /// </summary>
    private void RefreshWorldCount()
    {
        var count = 0;

        try
        {
            var saves = WorldManager.SavesDirectory(InstanceDirectory);

            if (Directory.Exists(saves))
            {
                count = Directory.EnumerateDirectories(saves)
                    .Count(d => File.Exists(Path.Combine(d, WorldInfo.LevelFileName)));
            }
        }
        catch (Exception)
        {
            // An unreadable saves folder shows as no worlds; opening the tab says why.
        }

        WorldCount = count;
    }

    /// <summary>Another build was selected: its worlds are not the ones on screen.</summary>
    private void OnWorldsBuildChanged()
    {
        _worldsRun++;
        Worlds.Clear();
        TrashedWorlds.Clear();
        TrashedWorldCount = 0;
        IsWorldsTrashOpen = false;
        WorldsStatus = string.Empty;
        RefreshWorldCount();

        if (BuildTab == BuildTab.Worlds)
        {
            RefreshWorlds();
        }
    }

    /// <summary>The game started or exited: the lock on editing follows, and after a session the worlds have changed.</summary>
    private void OnWorldsGameStateChanged()
    {
        OnPropertyChanged(nameof(CanEditWorlds));

        if (IsGameRunning)
        {
            return;
        }

        if (BuildTab == BuildTab.Worlds)
        {
            RefreshWorlds();
        }
        else
        {
            RefreshWorldCount();
        }
    }

    private int _worldsRun;

    /// <summary>
    /// Reads the list in the background and shows it; then, still in the background,
    /// fills in what takes disk work - icons, backups, sizes - card by card.
    /// </summary>
    private void RefreshWorlds()
    {
        var run = ++_worldsRun;
        var instance = SelectedInstance;
        var directory = InstanceDirectory;
        var backupsDirectory = instance is null ? null : WorldBackupsDirectory(instance);
        var service = WorldsService;

        IsWorldsLoading = Worlds.Count == 0;

        _ = Task.Run(async () =>
        {
            IReadOnlyList<WorldInfo> list;
            IReadOnlyList<TrashedWorld> trash;
            IReadOnlyList<WorldBackupInfo> backups;

            try
            {
                // A world deleted more than the retention ago goes for good here. The
                // player was told the term when they deleted it.
                service.PurgeExpiredTrash(directory, DateTimeOffset.Now);

                list = service.List(directory);
                trash = service.ListTrash(directory);
                backups = backupsDirectory is null ? Array.Empty<WorldBackupInfo>() : service.ListBackups(backupsDirectory);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (run == _worldsRun)
                    {
                        IsWorldsLoading = false;
                        WorldsStatus = Localize("Worlds_ListFailed", "Could not read the worlds: {0}", ex.Message);
                    }
                });
                return;
            }

            List<WorldItem> items = new();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (run != _worldsRun)
                {
                    return;
                }

                // What was already worked out for a world stays on its card while the
                // new pass runs, so a refresh after an action does not blink.
                var previous = Worlds.ToDictionary(w => w.Info.Directory, StringComparer.OrdinalIgnoreCase);

                foreach (var info in list)
                {
                    var item = new WorldItem(this, info);

                    if (previous.TryGetValue(info.Directory, out var old))
                    {
                        item.Icon = old.Icon;
                        item.SizeBytes = old.SizeBytes;
                        item.IsBackupsOpen = old.IsBackupsOpen;
                    }

                    ApplyWorldBackups(item, backups);
                    items.Add(item);
                }

                Worlds.Clear();

                foreach (var item in items)
                {
                    Worlds.Add(item);
                }

                TrashedWorlds.Clear();

                foreach (var trashed in trash)
                {
                    TrashedWorlds.Add(new TrashedWorldItem(this, trashed));
                }

                TrashedWorldCount = TrashedWorlds.Count;
                IsWorldsTrashOpen = IsWorldsTrashOpen && TrashedWorldCount > 0;
                WorldCount = items.Count;
                IsWorldsLoading = false;
            });

            // Icons first: they are small and make the list look finished. Sizes walk
            // every region file of every world and come last.
            foreach (var item in items)
            {
                if (run != _worldsRun)
                {
                    return;
                }

                if (item.Icon is not null || item.Info.IconPath is not { } iconPath)
                {
                    continue;
                }

                try
                {
                    using var stream = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var icon = Bitmap.DecodeToWidth(stream, 128);
                    Dispatcher.UIThread.Post(() => item.Icon = icon);
                }
                catch (Exception)
                {
                    // A half-written icon leaves the placeholder.
                }
            }

            foreach (var item in items)
            {
                if (run != _worldsRun)
                {
                    return;
                }

                try
                {
                    var size = WorldManager.MeasureSize(item.Info.Directory);
                    Dispatcher.UIThread.Post(() => item.SizeBytes = size);
                }
                catch (Exception)
                {
                    // The folder went away mid-count; the next refresh will not list it.
                }
            }
        });
    }

    private void ApplyWorldBackups(WorldItem item, IReadOnlyList<WorldBackupInfo> all)
    {
        item.Backups.Clear();

        foreach (var backup in all.Where(b => string.Equals(b.FolderName, item.FolderName, StringComparison.OrdinalIgnoreCase)))
        {
            item.Backups.Add(new WorldBackupItem(this, item, backup));
        }

        item.BackupCount = item.Backups.Count;
        item.IsBackupsOpen = item.IsBackupsOpen && item.BackupCount > 0;
    }

    /// <summary>
    /// Runs one action on a background thread with the tab marked busy, and turns what
    /// can go wrong into a sentence. Returns false when the action did not happen.
    /// </summary>
    private async Task<bool> RunWorldActionAsync(WorldItem? item, string progress, Func<string, Instance?, string> action)
    {
        if (IsWorldsBusy)
        {
            WorldsStatus = Localize("Worlds_Wait", "Wait for the current action to finish");
            return false;
        }

        var instance = SelectedInstance;
        var directory = InstanceDirectory;

        try
        {
            IsWorldsBusy = true;
            WorldsStatus = progress;

            if (item is not null)
            {
                item.IsWorking = true;
            }

            var done = await Task.Run(() => action(directory, instance));
            WorldsStatus = done;
            return true;
        }
        catch (WorldBusyException ex)
        {
            WorldsStatus = ex.Reason == WorldBusyReason.GameRunning
                ? Localize("Worlds_BusyGame", "The game is running. Worlds can be changed once it is closed")
                : Localize("Worlds_BusyOpen", "This world is open in the game. Leave it there first");
            return false;
        }
        catch (WorldArchiveException ex)
        {
            WorldsStatus = ex.Problem switch
            {
                WorldArchiveProblem.NotAWorld => Localize("Worlds_ImportNotAWorld", "This archive is not a world: level.dat should be at its top or inside a single folder"),
                WorldArchiveProblem.UnsafePath => Localize("Worlds_ImportUnsafe", "The archive tries to write outside the world folder. Nothing was unpacked"),
                _ => Localize("Worlds_ImportTooLarge", "The archive unpacks into more than a world can take. Nothing was unpacked")
            };
            AppendConsole($"[worlds] {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            WorldsStatus = Localize("Worlds_Failed", "It did not work: {0}", ex.Message);
            AppendConsole($"[worlds] {ex}");
            return false;
        }
        finally
        {
            IsWorldsBusy = false;

            if (item is not null)
            {
                item.IsWorking = false;
            }

            // Whatever happened, the disk is the truth; and only if the player is still looking at that build.
            if (ReferenceEquals(instance, SelectedInstance))
            {
                RefreshWorlds();
            }
        }
    }

    public async Task RenameWorldAsync(WorldItem item)
    {
        var name = item.NameEdit.Trim();

        if (name.Length == 0 || string.Equals(name, item.Name, StringComparison.Ordinal))
        {
            item.IsRenaming = false;
            return;
        }

        var done = await RunWorldActionAsync(
            item,
            Localize("Worlds_Renaming", "Renaming…"),
            (directory, _) =>
            {
                WorldsService.Rename(directory, item.Info, name);
                return Localize("Worlds_Renamed", "The world is now called \"{0}\". Its folder kept its name", name);
            });

        if (done)
        {
            item.Name = name;
            item.IsRenaming = false;
        }
    }

    public Task DuplicateWorldAsync(WorldItem item)
    {
        var copyName = Localize("Worlds_CopyName", "{0} (copy)", item.Name);

        return RunWorldActionAsync(
            item,
            Localize("Worlds_Duplicating", "Copying \"{0}\"…", item.Name),
            (directory, _) =>
            {
                var copy = WorldsService.Duplicate(directory, item.Info, copyName);
                AppendConsole($"[worlds] duplicated {item.FolderName} as {copy.FolderName}");
                return Localize("Worlds_Duplicated", "Copy made: \"{0}\"", copy.Name);
            });
    }

    public Task ExportWorldAsync(WorldItem item, string zipPath)
        => RunWorldActionAsync(
            item,
            Localize("Worlds_Exporting", "Packing \"{0}\"…", item.Name),
            (_, _) =>
            {
                WorldsService.Export(item.Info, zipPath);
                AppendConsole($"[worlds] exported {item.FolderName} to {zipPath}");
                return Localize("Worlds_Exported", "\"{0}\" saved to {1}", item.Name, zipPath);
            });

    public Task ImportWorldAsync(string zipPath)
        => RunWorldActionAsync(
            null,
            Localize("Worlds_Importing", "Unpacking {0}…", Path.GetFileName(zipPath)),
            (directory, _) =>
            {
                var world = WorldsService.Import(directory, zipPath);
                AppendConsole($"[worlds] imported {zipPath} as {world.FolderName}");
                return Localize("Worlds_Imported", "World \"{0}\" added", world.Name);
            });

    public Task BackupWorldAsync(WorldItem item)
    {
        var maxCount = (int)BackupsMaxCount;
        var maxBytes = (long)BackupsMaxTotalMb * 1024 * 1024;

        return RunWorldActionAsync(
            item,
            Localize("Worlds_BackingUp", "Backing up \"{0}\"…", item.Name),
            (_, instance) =>
            {
                if (instance is null)
                {
                    throw new InvalidOperationException("No build is selected.");
                }

                var backups = WorldBackupsDirectory(instance);
                var backup = WorldsService.Backup(item.Info, backups);

                // The same limits the build's own backups keep to, applied to this world's copies.
                WorldsService.PruneBackups(backups, item.FolderName, maxCount, maxBytes);
                AppendConsole($"[worlds] backup {backup.Path}");
                return Localize("Worlds_BackedUp", "Backup of \"{0}\" made ({1})", item.Name, WorldSizeLabel(backup.Size));
            });
    }

    /// <summary>Always as a new world: the one being played is never replaced by an older self.</summary>
    public Task RestoreWorldBackupAsync(WorldItem item, WorldBackupItem backup)
    {
        var name = Localize("Worlds_RestoredName", "{0} (backup of {1})", item.Name, backup.Info.CreatedAt.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.CurrentCulture));

        return RunWorldActionAsync(
            item,
            Localize("Worlds_Restoring", "Bringing back the copy from {0}…", backup.DateLabel),
            (directory, _) =>
            {
                var world = WorldsService.RestoreBackup(directory, backup.Info, name);
                AppendConsole($"[worlds] restored {backup.Info.FileName} as {world.FolderName}");
                return Localize("Worlds_Restored", "The copy is back as a new world, \"{0}\". The current one was not touched", world.Name);
            });
    }

    public void AskDeleteWorld(WorldItem item)
    {
        foreach (var other in Worlds)
        {
            other.IsConfirmingDelete = false;
        }

        item.IsConfirmingDelete = true;
    }

    public Task DeleteWorldAsync(WorldItem item)
    {
        item.IsConfirmingDelete = false;

        return RunWorldActionAsync(
            item,
            Localize("Worlds_Deleting", "Moving \"{0}\" to the trash…", item.Name),
            (directory, _) =>
            {
                var trashed = WorldsService.MoveToTrash(directory, item.Info);
                AppendConsole($"[worlds] moved {item.FolderName} to {trashed.Directory}");
                return Localize("Worlds_Deleted", "\"{0}\" is in the launcher's trash until {1:dd.MM.yyyy}. It can be brought back below", item.Name, trashed.ExpiresAt.ToLocalTime());
            });
    }

    public Task RestoreTrashedWorldAsync(TrashedWorldItem item)
        => RunWorldActionAsync(
            null,
            Localize("Worlds_Undeleting", "Bringing \"{0}\" back…", item.Name),
            (directory, _) =>
            {
                var world = WorldsService.RestoreFromTrash(directory, item.Info);
                AppendConsole($"[worlds] restored {world.FolderName} from the trash");
                return Localize("Worlds_Undeleted", "\"{0}\" is back among the worlds", world.Name);
            });

    public async Task CopyWorldSeedAsync(WorldItem item)
    {
        if (!item.HasSeed)
        {
            return;
        }

        try
        {
            var clipboard = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
                ? window.Clipboard
                : null;

            if (clipboard is null)
            {
                return;
            }

            await clipboard.SetTextAsync(item.SeedLabel);
            WorldsStatus = Localize("Worlds_SeedCopied", "Seed of \"{0}\" copied: {1}", item.Name, item.SeedLabel);
        }
        catch (Exception ex)
        {
            WorldsStatus = Localize("Worlds_Failed", "It did not work: {0}", ex.Message);
        }
    }

    public void OpenWorldFolder(WorldItem item)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Info.Directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            WorldsStatus = Localize("Error_OpenFolder", "Failed to open the folder: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenSavesFolder() => OpenInstanceFolder(WorldManager.SavesFolder);

    [RelayCommand]
    private void ToggleWorldsTrash() => IsWorldsTrashOpen = !IsWorldsTrashOpen;

    [RelayCommand]
    private void DismissWorldsStatus() => WorldsStatus = string.Empty;
}
