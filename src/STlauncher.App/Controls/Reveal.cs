using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace STlauncher.App.Controls;

/// <summary>
/// <c>controls:Reveal.OnVisible="True"</c> makes a control fade and rise into place each
/// time it becomes visible: pages when the section changes, tabs, cards that appear.
/// Done with transitions rather than a keyframe animation: Avalonia has no keyframe
/// animator for RenderTransform, but it does transition between two TransformOperations,
/// the same mechanism Fluent uses for its own hover motion. The control is put into its
/// start state with transitions off, then released with them on.
/// </summary>
public static class Reveal
{
    public static readonly AttachedProperty<bool> OnVisibleProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("OnVisible", typeof(Reveal));

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(280);
    private static readonly TransformOperations Rest = TransformOperations.Parse("translateY(0px)");

    static Reveal()
    {
        OnVisibleProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
            {
                control.PropertyChanged += OnControlPropertyChanged;
            }
            else
            {
                control.PropertyChanged -= OnControlPropertyChanged;
            }
        });
    }

    public static bool GetOnVisible(Control control) => control.GetValue(OnVisibleProperty);

    public static void SetOnVisible(Control control, bool value) => control.SetValue(OnVisibleProperty, value);

    private static void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && sender is Control { IsVisible: true } control)
        {
            Play(control, offset: 16);
        }
    }

    /// <summary>Start state now, without motion; the rest state on the next pass, with it.</summary>
    public static void Play(Control control, double offset)
    {
        var transitions = control.Transitions;
        control.Transitions = null;
        control.Opacity = 0;
        control.RenderTransform = TransformOperations.Parse($"translateY({offset.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}px)");

        Dispatcher.UIThread.Post(() =>
        {
            control.Transitions = transitions ?? new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = Duration, Easing = new CubicEaseOut() },
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Duration, Easing = new CubicEaseOut() }
            };
            control.Opacity = 1;
            control.RenderTransform = Rest;
        }, DispatcherPriority.Render);
    }
}
