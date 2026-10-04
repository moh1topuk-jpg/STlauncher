using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Mods;

namespace STlauncher.App.ViewModels;

public enum ModsSortOrder
{
    /// <summary>Enabled first, then by name: what is going to load is at the top.</summary>
    Default,
    Name,
    Size,
    Added
}

/// <summary>
/// What makes a list of a hundred mods usable: each row under the mod's own title with
/// its icon rather than a file name, an order the player picks, and actions that take
/// several mods at once.
/// </summary>
public partial class MainWindowViewModel
{
    // ===================== Titles and icons from the jars =====================

    private sealed record CachedModDisplay(long Size, DateTime Stamp, string? Name, string? Version, Bitmap? Icon);

    /// <summary>Keyed by path without the .disabled suffix: switching a mod off does not change what it is.</summary>
    private readonly Dictionary<string, CachedModDisplay> _modDisplayCache = new(StringComparer.OrdinalIgnoreCase);

    private int _modDetailsRun;

    /// <summary>Shown at this size in the list; decoding a 512 px icon a hundred times over is memory for nothing.</summary>
    private const int ModIconPixels = 96;

    private static string DisplayKey(string path)
        => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^".disabled".Length] : path;

    /// <summary>
    /// Fills in titles, versions and icons after the list is on screen. Jars already read
    /// come from the cache at once; the rest are opened off the UI thread, so a folder of
    /// a hundred mods does not hold the page.
    /// </summary>
    private async Task LoadModDetailsAsync()
    {
        var run = ++_modDetailsRun;
        var items = InstalledMods.Where(m => m.IsMod).ToList();
        var pending = new List<InstalledModItem>();

        foreach (var item in items)
        {
            if (_modDisplayCache.TryGetValue(DisplayKey(item.Path), out var cached) &&
                cached.Size == item.Size && cached.Stamp == item.Added)
            {
                Apply(item, cached);
            }
            else
            {
                pending.Add(item);
            }
        }

        if (pending.Count > 0)
        {
            var read = await Task.Run(() => pending.Select(item =>
            {
                var info = ModMetadataReader.ReadDisplay(item.Path);
                return (Item: item, Entry: new CachedModDisplay(item.Size, item.Added, info?.Name, info?.Version, DecodeIcon(info?.Icon)));
            }).ToList());

            foreach (var (item, entry) in read)
            {
                _modDisplayCache[DisplayKey(item.Path)] = entry;
            }

            if (run != _modDetailsRun)
            {
                // The list was rebuilt meanwhile; the newer run finds all of this in the cache.
                return;
            }

            foreach (var (item, entry) in read)
            {
                Apply(item, entry);
            }
        }

        // Titles changed what "by name" means and what the search box matches.
        ApplyModsSort();
        ApplyModsFilter();

        static void Apply(InstalledModItem item, CachedModDisplay entry)
        {
            item.MetaName = entry.Name;
            item.MetaVersion = entry.Version;
            item.Icon = entry.Icon;
        }
    }

    /// <summary>A small icon stays as it is (pixel art must not be smoothed up); a large one is brought down.</summary>
    private static Bitmap? DecodeIcon(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);

            if (bitmap.PixelSize.Width <= ModIconPixels * 2)
            {
                return bitmap;
            }

            var height = Math.Max(1, (int)Math.Round(bitmap.PixelSize.Height * (ModIconPixels / (double)bitmap.PixelSize.Width)));
            var scaled = bitmap.CreateScaledBitmap(new PixelSize(ModIconPixels, height));
            bitmap.Dispose();
            return scaled;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ===================== Order =====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModsSortLabel))]
    private ModsSortOrder _modsSort = ModsSortOrder.Default;

    partial void OnModsSortChanged(ModsSortOrder value)
    {
        ApplyModsSort();
        PersistSettings();
    }

    public string ModsSortLabel => Localize("Mods_Sort", "Sort: {0}", (ModsSort switch
    {
        ModsSortOrder.Name => Localize("Mods_SortName", "By name"),
        ModsSortOrder.Size => Localize("Mods_SortSize", "By size"),
        ModsSortOrder.Added => Localize("Mods_SortAdded", "By date added"),
        _ => Localize("Mods_SortDefault", "Enabled first")
    }).ToLower(System.Globalization.CultureInfo.CurrentCulture));

    [RelayCommand]
    private void SelectModsSort(string? order)
    {
        if (Enum.TryParse<ModsSortOrder>(order, ignoreCase: true, out var parsed))
        {
            ModsSort = parsed;
        }
    }

    /// <summary>Reorders the list in place, moving only the rows that are out of position.</summary>
    private void ApplyModsSort()
    {
        var byName = StringComparer.CurrentCultureIgnoreCase;

        var ordered = (ModsSort switch
        {
            ModsSortOrder.Name => InstalledMods.OrderBy(m => m.DisplayName, byName),
            ModsSortOrder.Size => InstalledMods.OrderByDescending(m => m.Size).ThenBy(m => m.DisplayName, byName),
            ModsSortOrder.Added => InstalledMods.OrderByDescending(m => m.Added).ThenBy(m => m.DisplayName, byName),
            _ => InstalledMods.OrderByDescending(m => m.Enabled).ThenBy(m => m.DisplayName, byName)
        }).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = InstalledMods.IndexOf(ordered[i]);

            if (current != i)
            {
                InstalledMods.Move(current, i);
            }
        }

        RebuildModRows();
    }

    // ===================== Several at once =====================

    /// <summary>True while rows show a tick box and the bar of actions is up.</summary>
    [ObservableProperty]
    private bool _isModSelectMode;

    /// <summary>The second press on "delete" is the one that deletes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BulkDeleteLabel))]
    private bool _isBulkDeleteArmed;

    /// <summary>Set while a loop switches many mods, so the list is rebuilt once at the end, not per file.</summary>
    private bool _deferModRefresh;

    public int SelectedModCount => InstalledMods.Count(m => m.IsSelected);

    public bool HasSelectedMods => SelectedModCount > 0;

    public string SelectedModsLabel => Localize("Mods_SelectedCount", "Selected: {0}", SelectedModCount);

    public string BulkDeleteLabel => IsBulkDeleteArmed
        ? Localize("Mods_BulkDeleteConfirm", "Really delete: {0}", SelectedModCount)
        : Localize("Mods_BulkDelete", "Delete");

    private void OnModItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledModItem.IsSelected))
        {
            IsBulkDeleteArmed = false;
            RaiseModSelection();
        }
    }

    private void RaiseModSelection()
    {
        OnPropertyChanged(nameof(SelectedModCount));
        OnPropertyChanged(nameof(HasSelectedMods));
        OnPropertyChanged(nameof(SelectedModsLabel));
        OnPropertyChanged(nameof(BulkDeleteLabel));
    }

    partial void OnIsModSelectModeChanged(bool value)
    {
        if (!value)
        {
            foreach (var mod in InstalledMods)
            {
                mod.IsSelected = false;
            }

            IsBulkDeleteArmed = false;
        }

        RaiseModSelection();
    }

    [RelayCommand]
    private void ToggleModSelectMode() => IsModSelectMode = !IsModSelectMode;

    /// <summary>Ticks every row the search and the slice leave on screen; a second press unticks them.</summary>
    [RelayCommand]
    private void SelectVisibleMods()
    {
        var visible = InstalledMods.Where(m => !m.IsHidden).ToList();
        var all = visible.Count > 0 && visible.All(m => m.IsSelected);

        foreach (var mod in visible)
        {
            mod.IsSelected = !all;
        }
    }

    [RelayCommand]
    private void EnableSelectedMods() => SwitchSelectedMods(enabled: true);

    [RelayCommand]
    private void DisableSelectedMods() => SwitchSelectedMods(enabled: false);

    private void SwitchSelectedMods(bool enabled)
    {
        if (IsGameRunning)
        {
            Status = Localize("Mods_BulkGameRunning", "Close the game first: it has the mod files open");
            return;
        }

        var targets = InstalledMods.Where(m => m.IsSelected && m.IsMod && m.Enabled != enabled).ToList();

        try
        {
            _deferModRefresh = true;

            foreach (var mod in targets)
            {
                ToggleMod(mod);
            }
        }
        finally
        {
            _deferModRefresh = false;
        }

        IsModSelectMode = false;
        RefreshMods();

        Status = enabled
            ? Localize("Mods_BulkEnabled", "Mods switched on: {0}", targets.Count)
            : Localize("Mods_BulkDisabled", "Mods switched off: {0}", targets.Count);
    }

    /// <summary>
    /// Deletes the ticked mods for good - the one action here that cannot be undone from
    /// the list, so the first press only arms it and says how many files it will take.
    /// </summary>
    [RelayCommand]
    private async Task DeleteSelectedModsAsync()
    {
        if (IsGameRunning)
        {
            Status = Localize("Mods_BulkGameRunning", "Close the game first: it has the mod files open");
            return;
        }

        var targets = InstalledMods.Where(m => m.IsSelected && m.IsMod).ToList();

        if (targets.Count == 0)
        {
            return;
        }

        if (!IsBulkDeleteArmed)
        {
            IsBulkDeleteArmed = true;
            return;
        }

        var removed = 0;

        try
        {
            await MaybeBackupAsync(BackupTrigger.BeforeModChange);

            foreach (var mod in targets)
            {
                try
                {
                    _mods.Uninstall(mod.Path);
                    SelectedInstance?.InstalledMods.RemoveAll(m =>
                        string.Equals(m.FileName, mod.FileName, StringComparison.OrdinalIgnoreCase));
                    AppendConsole($"[mods] deleted {mod.FileName}");
                    removed++;
                }
                catch (Exception ex)
                {
                    AppendConsole($"[mods] could not delete {mod.FileName}: {ex.Message}");
                }
            }

            if (SelectedInstance is not null)
            {
                _instances.Save(SelectedInstance);
            }
        }
        finally
        {
            IsModSelectMode = false;
            RefreshMods();
        }

        Status = Localize("Mods_BulkDeleted", "Mods deleted: {0}", removed);
    }
}
