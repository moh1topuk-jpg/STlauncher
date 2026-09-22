using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Backups;
using STlauncher.Core.Instances;

namespace STlauncher.App.ViewModels;

/// <summary>One backup on disk, with what is in it and the ways to bring it back.</summary>
public partial class BackupItem : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public BackupItem(MainWindowViewModel owner, BackupInfo info, string buildName, bool buildExists)
    {
        _owner = owner;
        Info = info;
        BuildName = buildName;
        BuildExists = buildExists;
        DateLabel = info.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        SizeLabel = info.Size >= 1024L * 1024
            ? MainWindowViewModel.Localize("Size_MbPrecise", "{0:F1} MB", info.Size / 1024d / 1024)
            : MainWindowViewModel.Localize("Size_Kb", "{0} KB", Math.Max(1, info.Size / 1024));
    }

    public BackupInfo Info { get; }

    /// <summary>The build the backup came from - its current name, or the one stored inside.</summary>
    public string BuildName { get; }

    /// <summary>False when that build has since been deleted: only "as a new build" makes sense then.</summary>
    public bool BuildExists { get; }

    public string DateLabel { get; }

    public string SizeLabel { get; }

    /// <summary>"3 worlds · 24 mods", filled in once the archive has been read.</summary>
    [ObservableProperty]
    private string _contentsLabel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestoreWorlds))]
    private bool _hasWorlds;

    [ObservableProperty]
    private bool _hasDefinition;

    public bool CanRestoreWorlds => HasWorlds && BuildExists;

    public bool CanRestoreEverything => BuildExists;

    [RelayCommand]
    private Task RestoreWorldsAsync() => _owner.RestoreBackupAsync(this, RestoreScope.Worlds);

    [RelayCommand]
    private Task RestoreEverythingAsync() => _owner.RestoreBackupAsync(this, RestoreScope.Everything);

    [RelayCommand]
    private Task RestoreAsNewBuildAsync() => _owner.RestoreBackupAsNewBuildAsync(this);

    [RelayCommand]
    private void Delete() => _owner.DeleteBackup(this);
}

/// <summary>
/// Bringing a backup back. The archive is the player's worlds plus the build around
/// them, so it can go back into the build it came from - just the worlds, or all of it -
/// or become a new build when the original is gone.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<BackupItem> Backups { get; } = new();

    [ObservableProperty]
    private bool _isRestoringBackup;

    public bool HasBackups => Backups.Count > 0;

    public void RefreshBackups()
    {
        Backups.Clear();

        var definitionsByPath = new System.Collections.Generic.Dictionary<string, Instance?>();

        foreach (var info in _backups.List(BackupsDirectory))
        {
            var instance = info.InstanceId is null
                ? null
                : _allInstances.FirstOrDefault(i => string.Equals(i.Id, info.InstanceId, StringComparison.OrdinalIgnoreCase));

            var item = new BackupItem(this, info, instance?.Name ?? info.InstanceId ?? info.FileName, instance is not null);
            Backups.Add(item);
        }

        OnPropertyChanged(nameof(HasBackups));

        // Reading each archive's table of contents is quick but is still disk work, and
        // a folder of fifty backups should not stall the settings page.
        var snapshot = Backups.ToList();
        _ = Task.Run(() => DescribeBackups(snapshot));
    }

    private void DescribeBackups(System.Collections.Generic.List<BackupItem> items)
    {
        foreach (var item in items)
        {
            BackupContents contents;

            try
            {
                contents = _backups.Inspect(item.Info.Path);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => item.ContentsLabel = Localize("Backup_Unreadable", "cannot be read: {0}", ex.Message));
                continue;
            }

            var parts = new System.Collections.Generic.List<string>();

            if (contents.Worlds > 0)
            {
                parts.Add(Localize("Backup_Worlds", "{0} world(s)", contents.Worlds));
            }

            if (contents.Mods > 0)
            {
                parts.Add(Localize("Backup_Mods", "{0} mod(s)", contents.Mods));
            }

            if (contents.Definition is { } definition)
            {
                parts.Add($"{definition.VersionId ?? "?"} · {definition.Loader}");
            }

            var label = parts.Count == 0
                ? Localize("Backup_Empty", "empty")
                : string.Join(" · ", parts);

            Dispatcher.UIThread.Post(() =>
            {
                item.ContentsLabel = label;
                item.HasWorlds = contents.Worlds > 0;
                item.HasDefinition = contents.Definition is not null;
            });
        }
    }

    /// <summary>
    /// Puts a backup back into the build it came from. The current state is backed up
    /// first: a restore is exactly the kind of moment one wants an undo for.
    /// </summary>
    public async Task RestoreBackupAsync(BackupItem item, RestoreScope scope)
    {
        if (IsRestoringBackup || IsGameRunning || IsBusy)
        {
            BackupStatus = Localize("Backup_RestoreBusy", "Wait for the game and the launcher to finish first");
            return;
        }

        var target = _allInstances.FirstOrDefault(i =>
            string.Equals(i.Id, item.Info.InstanceId, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            BackupStatus = Localize("Backup_BuildGone", "That build no longer exists - restore the backup as a new build");
            return;
        }

        try
        {
            IsRestoringBackup = true;
            var directory = _instances.GameDirectory(target);

            BackupStatus = Localize("Backup_SafetyCopy", "Backing up the current state of \"{0}\" first…", target.Name);
            await _backups.CreateAsync(directory, BackupsDirectory, target.Id, target);

            BackupStatus = Localize("Backup_Restoring", "Restoring \"{0}\"…", target.Name);
            await _backups.RestoreAsync(item.Info.Path, directory, scope);

            if (scope == RestoreScope.Everything)
            {
                // The mods folder changed under the records; re-read what is there.
                target.InstalledMods.RemoveAll(m =>
                    !System.IO.File.Exists(System.IO.Path.Combine(directory, "mods", m.FileName)));
                _instances.Save(target);
            }

            if (string.Equals(target.Id, SelectedInstance?.Id, StringComparison.OrdinalIgnoreCase))
            {
                RefreshMods();
            }

            PruneBackups();
            RefreshBackups();

            BackupStatus = scope == RestoreScope.Worlds
                ? Localize("Backup_RestoredWorlds", "Worlds from {0} are back in \"{1}\"", item.DateLabel, target.Name)
                : Localize("Backup_RestoredAll", "\"{0}\" is back to how it was on {1}", target.Name, item.DateLabel);

            AppendConsole($"[backup] restored {item.Info.FileName} into {target.Id} ({scope})");
        }
        catch (Exception ex)
        {
            BackupStatus = Localize("Backup_RestoreFailed", "Restore failed: {0}", ex.Message);
            AppendConsole($"[backup] restore failed: {ex}");
        }
        finally
        {
            IsRestoringBackup = false;
        }
    }

    /// <summary>
    /// Turns a backup into a build of its own: the definition inside gives it its
    /// version and loader, and the archive gives it everything else.
    /// </summary>
    public async Task RestoreBackupAsNewBuildAsync(BackupItem item)
    {
        if (IsRestoringBackup || IsGameRunning || IsBusy)
        {
            BackupStatus = Localize("Backup_RestoreBusy", "Wait for the game and the launcher to finish first");
            return;
        }

        try
        {
            IsRestoringBackup = true;

            var definition = _backups.ReadDefinition(item.Info.Path);
            var baseName = definition?.Name is { Length: > 0 } name ? name : item.BuildName;
            var instance = _instances.Create(UniqueInstanceName(Localize("Backup_NewBuildName", "{0} (from backup {1})", baseName, item.DateLabel)));

            if (definition is not null)
            {
                instance.VersionId = definition.VersionId;
                instance.Loader = definition.Loader;
                instance.LoaderVersion = definition.LoaderVersion;
                instance.MaxMemoryMb = definition.MaxMemoryMb;
                instance.MinMemoryMb = definition.MinMemoryMb;
                instance.Width = definition.Width;
                instance.Height = definition.Height;
                instance.ExtraGameArgs = definition.ExtraGameArgs;
                instance.ProfileVersionId = definition.ProfileVersionId;
                instance.ServerName = definition.ServerName;
                instance.ServerAddress = definition.ServerAddress;
                instance.InstalledMods = definition.InstalledMods.ToList();

                // Deliberately not carried over: a restored copy is the player's own from
                // here on, and the catalog sync must not rewrite it.
                instance.CatalogBuildId = null;
                instance.EnabledCatalogItems = new System.Collections.Generic.List<string>();
            }

            _instances.Save(instance);

            BackupStatus = Localize("Backup_Restoring", "Restoring \"{0}\"…", instance.Name);
            await _backups.RestoreAsync(item.Info.Path, _instances.GameDirectory(instance), RestoreScope.Everything);

            _allInstances.Add(instance);
            ApplyBuildFilter();
            SelectedInstance = Instances.FirstOrDefault(i => i.Id == instance.Id);
            RefreshBackups();

            BackupStatus = definition is null
                ? Localize("Backup_RestoredNewNoVersion", "Build \"{0}\" created - pick its version and loader in the build settings", instance.Name)
                : Localize("Backup_RestoredNew", "Build \"{0}\" created from the backup", instance.Name);

            AppendConsole($"[backup] restored {item.Info.FileName} as new build {instance.Id}");
        }
        catch (Exception ex)
        {
            BackupStatus = Localize("Backup_RestoreFailed", "Restore failed: {0}", ex.Message);
            AppendConsole($"[backup] restore failed: {ex}");
        }
        finally
        {
            IsRestoringBackup = false;
        }
    }

    public void DeleteBackup(BackupItem item)
    {
        if (_backups.Delete(item.Info.Path))
        {
            Backups.Remove(item);
            OnPropertyChanged(nameof(HasBackups));
            BackupStatus = Localize("Backup_Deleted", "Backup from {0} deleted", item.DateLabel);
        }
        else
        {
            BackupStatus = Localize("Backup_DeleteFailed", "Could not delete the backup - it may be in use");
        }
    }
}
