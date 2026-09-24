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

    /// <summary>Lists the folder at once; thumbnails arrive one by one from a background decode.</summary>
    private void RefreshScreenshots()
    {
        var run = ++_screenshotsRun;
        var directory = InstanceDirectory;

        Screenshots.Clear();

        var list = ScreenshotFolder.List(directory);
        var items = list.Select(s => new ScreenshotItem(s)).ToList();

        foreach (var item in items)
        {
            Screenshots.Add(item);
        }

        ScreenshotCount = items.Count;

        _ = Task.Run(async () =>
        {
            foreach (var item in items)
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
