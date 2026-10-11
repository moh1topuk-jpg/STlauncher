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

    /// <summary>
    /// Keyed by path without the .disabled suffix: switching a mod off does not change what it is.
    /// Filled from the pass that reads the jars, off the interface thread, so that a pass
    /// started while another is still reading finds what that one has already read.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedModDisplay> _modDisplayCache = new(StringComparer.OrdinalIgnoreCase);

    private int _modDetailsRun;

    /// <summary>The pass reading jars now, or the last one: the next waits for it instead of reading the same jars beside it.</summary>
    private Task _modDetailsWork = Task.CompletedTask;

    /// <summary>Shown at this size in the list; decoding a 512 px icon a hundred times over is memory for nothing.</summary>
    private const int ModIconPixels = 96;

    private static string DisplayKey(string path)
        => path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? path[..^".disabled".Length] : path;

    /// <summary>Where the titles and icons of a build's mods are kept between launches.</summary>
    private string? ModDisplayStorePath(STlauncher.Core.Instances.Instance? instance)
        => instance is null || string.IsNullOrWhiteSpace(instance.Id)
            ? null
            : Path.Combine(_paths.Root, "cache", "mod-display", instance.Id + ".json");

    /// <summary>
    /// Fills in titles, versions and icons after the list is on screen. Jars already read
    /// come from the cache at once; the rest are opened off the UI thread, so a folder of
    /// a hundred mods does not hold the page - and only the first time: what was read is
    /// written down, and the next start takes it from one small file.
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
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var storePath = ModDisplayStorePath(SelectedInstance);
            var present = items.Select(i => i.FileName).ToList();
            var previous = _modDetailsWork;

            var work = Task.Run(async () =>
            {
                // One pass at a time. The list is rebuilt in bursts - a start, an install of
                // several mods - and each rebuild used to open every jar beside the others.
                try
                {
                    await previous.ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                return ReadModDisplays(pending, present, storePath, () => run != _modDetailsRun);
            });

            _modDetailsWork = work;

            var read = await work;

            if (run != _modDetailsRun)
            {
                // The list was rebuilt meanwhile; the newer run finds all of this in the cache.
                return;
            }

            foreach (var (item, entry) in read.Entries)
            {
                Apply(item, entry);
            }

            AppendConsole($"[mods] titles and icons: {read.Entries.Count} mod(s) in {clock.ElapsedMilliseconds} ms ({read.FromJars} jar(s) opened, the rest remembered)");
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

    private sealed record ModDisplayPass(List<(InstalledModItem Item, CachedModDisplay Entry)> Entries, int FromJars);

    /// <summary>
    /// The pass itself, on a pool thread: memory first, then the file kept from earlier
    /// launches, then the jar. Stops early when a newer list has replaced this one - what
    /// it has read by then stays in memory for the pass that follows.
    /// </summary>
    private ModDisplayPass ReadModDisplays(List<InstalledModItem> pending, List<string> present, string? storePath, Func<bool> superseded)
    {
        var entries = new List<(InstalledModItem, CachedModDisplay)>(pending.Count);
        ModDisplayCache? store = null;
        var fromJars = 0;
        var complete = true;

        foreach (var item in pending)
        {
            if (superseded())
            {
                complete = false;
                break;
            }

            var key = DisplayKey(item.Path);

            if (_modDisplayCache.TryGetValue(key, out var known) && known.Size == item.Size && known.Stamp == item.Added)
            {
                entries.Add((item, known));
                continue;
            }

            if (store is null && storePath is not null)
            {
                store = ModDisplayCache.Load(storePath);
            }

            CachedModDisplay entry;

            if (store is not null && store.TryGet(item.FileName, item.Size, item.Added.Ticks, out var stored))
            {
                entry = new CachedModDisplay(item.Size, item.Added, stored.Name, stored.Version, DecodeIcon(stored.Icon, out _));
            }
            else
            {
                var info = ModMetadataReader.ReadDisplay(item.Path);
                var icon = DecodeIcon(info?.Icon, out var compact);

                entry = new CachedModDisplay(item.Size, item.Added, info?.Name, info?.Version, icon);
                store?.Set(item.FileName, new ModDisplayEntry(item.Size, item.Added.Ticks, info?.Name, info?.Version, compact));
                fromJars++;
            }

            _modDisplayCache[key] = entry;
            entries.Add((item, entry));
        }

        // Only a pass that saw the whole folder may say which files have left it.
        store?.Save(complete ? present : null);

        return new ModDisplayPass(entries, fromJars);
    }

    private static Bitmap? DecodeIcon(byte[]? bytes) => DecodeIcon(bytes, out _);

    /// <summary>A small icon stays as it is (pixel art must not be smoothed up); a large one is brought down.</summary>
    /// <param name="compact">The icon at the size it is shown, as an image file's bytes: what is worth keeping between launches.</param>
    private static Bitmap? DecodeIcon(byte[]? bytes, out byte[]? compact)
    {
        compact = null;

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
                compact = bytes;
                return bitmap;
            }

            var height = Math.Max(1, (int)Math.Round(bitmap.PixelSize.Height * (ModIconPixels / (double)bitmap.PixelSize.Width)));
            var scaled = bitmap.CreateScaledBitmap(new PixelSize(ModIconPixels, height));
            bitmap.Dispose();

            try
            {
                using var encoded = new MemoryStream();
                scaled.Save(encoded);
                compact = encoded.ToArray();
            }
            catch (Exception)
            {
                // The icon is on screen either way; it is only read from the jar again next time.
            }

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
