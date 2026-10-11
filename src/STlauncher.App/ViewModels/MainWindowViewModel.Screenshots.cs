using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Screenshots;

namespace STlauncher.App.ViewModels;

/// <summary>A screenshot tile: a small decode for the grid, the file for everything else.</summary>
public partial class ScreenshotItem : ObservableObject
{
    public ScreenshotItem(ScreenshotInfo info)
    {
        Info = info;
        DateLabel = info.TakenAt.ToString("d MMM yyyy, HH:mm");
    }

    public ScreenshotInfo Info { get; }

    public string Path => Info.Path;

    public string FileName => Info.FileName;

    public long Size => Info.Size;

    public string DateLabel { get; }

    [ObservableProperty]
    private Bitmap? _thumbnail;

    /// <summary>Delete asks once, on the tile itself, instead of a dialog.</summary>
    [ObservableProperty]
    private bool _isConfirmingDelete;
}

/// <summary>
/// The build's screenshots: a grid of the pictures the game saved, newest first. A click
/// opens the full picture; copy puts the file on the clipboard, which is what a chat
/// wants; delete asks once on the tile.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<ScreenshotItem> Screenshots { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoScreenshots))]
    [NotifyPropertyChangedFor(nameof(ScreenshotsTabLabel))]
    private int _screenshotCount;

    public bool HasNoScreenshots => ScreenshotCount == 0;

    public string ScreenshotsTabLabel => TabLabel("Builds_TabScreenshots", "Screenshots", ScreenshotCount);

    private int _screenshotsRun;

    /// <summary>A decoded thumbnail and the file it was decoded from, as it was then.</summary>
    private sealed record CachedThumbnail(DateTime TakenAt, long Size, Bitmap Image);

    /// <summary>
    /// The thumbnails of the build on screen, by file path. Opening the tab used to decode
    /// every screenshot again, each time, and leave the previous set to the collector; now
    /// a picture is decoded once for as long as the build stays selected and its file
    /// stays the same. Let go of when another build is selected and when the game starts.
    /// </summary>
    private readonly System.Collections.Generic.Dictionary<string, CachedThumbnail> _thumbnails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The build folder <see cref="_thumbnails"/> belongs to.</summary>
    private string? _thumbnailsDirectory;

    private bool IsScreenshotsTabOnScreen => Section == ShellSection.Builds && BuildTab == BuildTab.Screenshots;

    /// <summary>
    /// Another build was selected: its count for the tab label, read off this thread, and
    /// its pictures if the tab is the one open. The previous build's thumbnails are freed.
    /// </summary>
    private void OnScreenshotsBuildChanged()
    {
        var directory = InstanceDirectory;

        if (!string.Equals(_thumbnailsDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseScreenshotThumbnails();
        }

        if (BuildTab == BuildTab.Screenshots)
        {
            RefreshScreenshots();
            return;
        }

        _ = CountScreenshotsAsync(directory);
    }

    /// <summary>The number on the tab. A folder of a thousand pictures is a thousand file lookups: not for the interface thread.</summary>
    private async Task CountScreenshotsAsync(string directory)
    {
        int count;

        try
        {
            count = await Task.Run(() => ScreenshotFolder.List(directory).Count);
        }
        catch (Exception)
        {
            count = 0;
        }

        // Only if the build is still the one that was asked about.
        if (string.Equals(InstanceDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            ScreenshotCount = count;
        }
    }

    /// <summary>
    /// Takes the tiles off the screen and frees their thumbnails. The list is emptied
    /// first: a picture is only disposed once nothing shows it.
    /// </summary>
    private void ReleaseScreenshotThumbnails()
    {
        _screenshotsRun++;
        Screenshots.Clear();

        foreach (var cached in _thumbnails.Values)
        {
            cached.Image.Dispose();
        }

        _thumbnails.Clear();
        _thumbnailsDirectory = null;
    }

    /// <summary>The tab was left: whatever is still being decoded for it stops after the picture in hand.</summary>
    private void PauseScreenshots() => _screenshotsRun++;

    /// <summary>Lists the folder at once; thumbnails arrive one by one from a background decode.</summary>
    private void RefreshScreenshots()
    {
        var run = ++_screenshotsRun;
        var directory = InstanceDirectory;

        Screenshots.Clear();

        if (!string.Equals(_thumbnailsDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseScreenshotThumbnails();
            run = ++_screenshotsRun;
            _thumbnailsDirectory = directory;
        }

        var list = ScreenshotFolder.List(directory);
        var items = list.Select(s => new ScreenshotItem(s)).ToList();
        var pending = new System.Collections.Generic.List<ScreenshotItem>();
        var present = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            present.Add(item.Path);

            // The same file as when it was decoded: the thumbnail is on the tile at once.
            if (_thumbnails.TryGetValue(item.Path, out var cached) &&
                cached.TakenAt == item.Info.TakenAt && cached.Size == item.Size)
            {
                item.Thumbnail = cached.Image;
            }
            else
            {
                pending.Add(item);
            }

            Screenshots.Add(item);
        }

        // Pictures deleted or replaced since: nothing shows their thumbnails any more.
        foreach (var gone in _thumbnails.Where(t => !present.Contains(t.Key) || pending.Any(p => string.Equals(p.Path, t.Key, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _thumbnails.Remove(gone.Key);
            gone.Value.Image.Dispose();
        }

        ScreenshotCount = items.Count;

        if (pending.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var item in pending)
            {
                if (run != _screenshotsRun)
                {
                    return;
                }

                Bitmap? thumbnail = null;

                try
                {
                    using var stream = File.OpenRead(item.Path);
                    thumbnail = Bitmap.DecodeToWidth(stream, 360);
                }
                catch (Exception)
                {
                    // A half-written or corrupt file shows as a blank tile.
                }

                if (thumbnail is not null)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // Decoded for a list that is gone, or for another build: nobody will show it.
                        if (!string.Equals(_thumbnailsDirectory, directory, StringComparison.OrdinalIgnoreCase))
                        {
                            thumbnail.Dispose();
                            return;
                        }

                        // An earlier pass finished this very picture a moment ago: one copy is enough.
                        if (_thumbnails.TryGetValue(item.Path, out var known))
                        {
                            thumbnail.Dispose();

                            if (run == _screenshotsRun)
                            {
                                item.Thumbnail = known.Image;
                            }

                            return;
                        }

                        // Kept even when the tab was left meanwhile: the work is done, and
                        // the tile has it the moment the tab is opened again.
                        _thumbnails[item.Path] = new CachedThumbnail(item.Info.TakenAt, item.Size, thumbnail);

                        if (run == _screenshotsRun)
                        {
                            item.Thumbnail = thumbnail;
                        }
                    });
                }
            }
        });
    }

    [RelayCommand]
    private void OpenScreenshot(ScreenshotItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(item.Path);
            var full = new Bitmap(stream);

            // The viewer made this picture and frees it when it shows another or closes.
            ShowLightboxFile(full, item.Path, item.FileName);
        }
        catch (Exception ex)
        {
            Status = Localize("Shots_OpenFailed", "Could not open the screenshot: {0}", ex.Message);
        }
    }

    /// <summary>The file goes on the clipboard: a chat window pastes it as a picture, Explorer as a file.</summary>
    [RelayCommand]
    private async Task CopyScreenshotAsync(ScreenshotItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                return;
            }

            var file = await window.StorageProvider.TryGetFileFromPathAsync(item.Path);

            if (file is null || window.Clipboard is null)
            {
                return;
            }

            // The DataObject API is marked obsolete in 11.3 but is the one that puts a file on
            // the Windows clipboard the way Explorer does; its successor is still settling.
#pragma warning disable CS0618
            var data = new DataObject();
            data.Set(DataFormats.Files, new IStorageItem[] { file });
            await window.Clipboard.SetDataObjectAsync(data);
#pragma warning restore CS0618
            Status = Localize("Shots_Copied", "Screenshot copied: paste it into a chat");
        }
        catch (Exception ex)
        {
            Status = Localize("Shots_CopyFailed", "Could not copy: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteScreenshot(ScreenshotItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.IsConfirmingDelete)
        {
            foreach (var other in Screenshots)
            {
                other.IsConfirmingDelete = false;
            }

            item.IsConfirmingDelete = true;
            return;
        }

        try
        {
            ScreenshotFolder.Delete(item.Path);
            Screenshots.Remove(item);

            if (_thumbnails.Remove(item.Path, out var cached))
            {
                cached.Image.Dispose();
            }

            ScreenshotCount = Screenshots.Count;
        }
        catch (Exception ex)
        {
            Status = Localize("Shots_DeleteFailed", "Could not delete: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private void CancelDeleteScreenshot(ScreenshotItem? item)
    {
        if (item is not null)
        {
            item.IsConfirmingDelete = false;
        }
    }

    [RelayCommand]
    private void OpenScreenshotsFolder() => OpenInstanceFolder(ScreenshotFolder.FolderName);
}
