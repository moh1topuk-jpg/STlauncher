using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;

namespace STlauncher.App.Controls;

/// <summary>
/// <c>controls:Reveal.OnVisible="True"</c> makes a control fade and rise into place each
/// time it becomes visible: pages when the section changes, tabs, cards that appear.
/// Short and one-directional, so it reads as arrival, never as a wait.
/// </summary>
public static class Reveal
{
    public static readonly AttachedProperty<bool> OnVisibleProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("OnVisible", typeof(Reveal));

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(260);

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
            _ = PlayAsync(control);
        }
    }

    private static async Task PlayAsync(Control control)
    {
        var animation = new Animation
        {
            Duration = Duration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                        new Setter(Visual.RenderTransformProperty, TransformOperations.Parse("translateY(14px)"))
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(Visual.RenderTransformProperty, TransformOperations.Parse("translateY(0px)"))
                    }
                }
            }
        };

        try
        {
            await animation.RunAsync(control);
        }
        catch (Exception)
        {
            // A control removed mid-animation is not worth a crash.
        }
    }
}
