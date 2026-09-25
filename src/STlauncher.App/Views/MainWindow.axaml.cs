using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace STlauncher.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ApplyBrandIcon();
        FitToScreen();
        SizeChanged += (_, _) => UpdateCompactScale();

        // The mark flies to the rail logo, wherever the layout puts it.
        Splash.LayoutUpdated += (_, _) =>
        {
            if (RailLogo.Bounds.Width > 0 &&
                Avalonia.VisualExtensions.TranslatePoint(RailLogo, new Avalonia.Point(RailLogo.Bounds.Width / 2, RailLogo.Bounds.Height / 2), Splash) is { X: > 0, Y: > 0 } centre)
            {
                Splash.RailLogoCentre = centre;
            }
        };
    }

    /// <summary>
    /// The default size is for a desktop; a laptop at 125 % has about 1090×580 to spare,
    /// and a window that does not fit is the first thing a new player hits.
    /// </summary>
    private void FitToScreen()
    {
        try
        {
            if (Screens.Primary is not { } screen)
            {
                return;
            }

            var area = screen.WorkingArea;
            var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
            var maxWidth = area.Width / scaling - 24;
            var maxHeight = area.Height / scaling - 24;

            Width = Math.Max(MinWidth, Math.Min(Width, maxWidth));
            Height = Math.Max(MinHeight, Math.Min(Height, maxHeight));
        }
        catch (Exception)
        {
            // No screen information: the default size stands.
        }
    }

    /// <summary>Below a comfortable height the interface shrinks as a whole, never by clipping.</summary>
    private void UpdateCompactScale()
    {
        var height = ClientSize.Height;
        var width = ClientSize.Width;
        var scale = height < 620 || width < 980 ? 0.86
            : height < 700 || width < 1080 ? 0.93
            : 1.0;

        var current = Zoom.LayoutTransform is Avalonia.Media.ScaleTransform existing ? existing.ScaleX : 1.0;

        if (Math.Abs(current - scale) < 0.001)
        {
            return;
        }

        Zoom.LayoutTransform = scale >= 0.999 ? null : new Avalonia.Media.ScaleTransform(scale, scale);
    }

    /// <summary>
    /// Uses Assets/server-logo.png as the window icon when it is present; the built-in
    /// icon stays otherwise, so the artwork is optional.
    /// </summary>
    private void ApplyBrandIcon()
    {
        try
        {
            var uri = new Uri("avares://STlauncher.App/Assets/server-logo.png");
            using var stream = AssetLoader.Open(uri);
            Icon = new WindowIcon(new Bitmap(stream));
        }
        catch (Exception)
        {
        }
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        BeginMoveDrag(e);
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
