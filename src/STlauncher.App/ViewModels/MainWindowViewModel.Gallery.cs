using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace STlauncher.App.ViewModels;

/// <summary>
/// The screenshot viewer for a catalog project: a thumbnail in the details card opens the
/// same image over the whole window, with arrows to the next one. Modrinth serves the
/// gallery at full size, so the bitmap the thumbnail already holds is the one shown.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Full-size URLs behind <see cref="OpenedProjectGallery"/>, in the same order.</summary>
    private readonly List<string> _galleryUrls = new();

    [ObservableProperty]
    private bool _isLightboxOpen;

    [ObservableProperty]
    private Bitmap? _lightboxImage;

    /// <summary>True while the full-size original is still on its way; the preview shows meanwhile.</summary>
    [ObservableProperty]
    private bool _isLightboxLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LightboxCounter))]
    [NotifyPropertyChangedFor(nameof(HasPreviousLightboxImage))]
    [NotifyPropertyChangedFor(nameof(HasNextLightboxImage))]
    private int _lightboxIndex;

    public string LightboxCounter => OpenedProjectGallery.Count == 0
        ? string.Empty
        : $"{LightboxIndex + 1} / {OpenedProjectGallery.Count}";

    public bool HasPreviousLightboxImage => LightboxIndex > 0;

    public bool HasNextLightboxImage => LightboxIndex < OpenedProjectGallery.Count - 1;

    /// <summary>The full description is long for most projects; the card shows a preview first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DescriptionMaxHeight))]
    private bool _isDescriptionExpanded;

    public double DescriptionMaxHeight => IsDescriptionExpanded ? double.PositiveInfinity : 190;

    [RelayCommand]
    private void ToggleDescription() => IsDescriptionExpanded = !IsDescriptionExpanded;

    [RelayCommand]
    private void OpenGalleryImage(Bitmap? image)
    {
        var index = image is null ? -1 : OpenedProjectGallery.IndexOf(image);

        if (index < 0)
        {
            return;
        }

        ShowLightbox(index);
    }

    [RelayCommand]
    private void CloseLightbox()
    {
        IsLightboxOpen = false;
        LightboxImage = null;
    }

    [RelayCommand]
    private void NextLightboxImage() => ShowLightbox(LightboxIndex + 1);

    [RelayCommand]
    private void PreviousLightboxImage() => ShowLightbox(LightboxIndex - 1);

    /// <summary>The image in the browser, for saving or zooming beyond the window.</summary>
    [RelayCommand]
    private void OpenLightboxInBrowser()
    {
        if (LightboxIndex >= 0 && LightboxIndex < _galleryUrls.Count)
        {
            OpenUrl(_galleryUrls[LightboxIndex]);
        }
    }

    private async void ShowLightbox(int index)
    {
        if (index < 0 || index >= OpenedProjectGallery.Count)
        {
            return;
        }

        LightboxIndex = index;
        LightboxImage = OpenedProjectGallery[index];
        IsLightboxOpen = true;

        if (index >= _galleryUrls.Count)
        {
            return;
        }

        try
        {
            IsLightboxLoading = true;
            var full = await _images.GetAsync(_galleryUrls[index]);

            // Only if the viewer is still on this picture: arrows may have moved on.
            if (full is not null && IsLightboxOpen && LightboxIndex == index)
            {
                LightboxImage = full;
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[gallery] full image failed: {ex.Message}");
        }
        finally
        {
            if (LightboxIndex == index)
            {
                IsLightboxLoading = false;
            }
        }
    }

    private void ResetGallery()
    {
        OpenedProjectGallery.Clear();
        _galleryUrls.Clear();
        IsDescriptionExpanded = false;
        CloseLightbox();
    }
}
