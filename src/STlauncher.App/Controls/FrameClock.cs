using System;
using Avalonia.Controls;
using Avalonia.Threading;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Controls;

/// <summary>
/// The slow clock of the launcher's decoration, and the one place that decides how much
/// of it a window is worth. The embers behind the main screen move by this clock; the
/// figure in front of them moves on every frame of the display while the window is in
/// front (see <see cref="SkinViewer"/>) and by this clock only while it is behind another.
/// Whatever has stopped looks a few times a second whether it may go on.
/// </summary>
/// <remarks>
/// The embers used to be redrawn on every frame of the screen, sixty or a hundred and
/// forty-four a second, and they cover the whole page: everything above them was drawn
/// again each time, for dots that drift eight pixels a second. They take a modest step
/// now. The figure is the opposite case - small, and watched - and gets every frame.
/// Neither timer here runs while nobody is subscribed.
///
/// The step is a thirtieth of a second asked of the system timer. Windows rounds that up
/// to its own grain, so on most machines it comes to some twenty-two steps a second.
/// Asking for a finer timer to hit thirty exactly was tried and cost more than the frames
/// it bought: every wake of the interface thread has a price, drawn frame or not.
/// </remarks>
internal static class FrameClock
{
    /// <summary>Enough for embers that drift eight pixels a second at most.</summary>
    public const double FrameMs = 1000.0 / 30;

    /// <summary>
    /// How often something that has stopped looks whether it may go on. Short enough that
    /// a page opened again is moving before the eye has settled on it; a look is a handful
    /// of property reads.
    /// </summary>
    private const double CheckMs = 125;

    private static DispatcherTimer? _frames;
    private static DispatcherTimer? _checks;
    private static Action? _frame;
    private static Action? _check;

    /// <summary>A step of the animation. Raised on the interface thread.</summary>
    public static event Action Frame
    {
        add
        {
            _frame += value;
            Sync(ref _frames, _frame, FrameMs, static () => _frame?.Invoke());
        }
        remove
        {
            _frame -= value;
            Sync(ref _frames, _frame, FrameMs, static () => _frame?.Invoke());
        }
    }

    /// <summary>The slow lane: a moment to see whether whatever stopped the animation has passed.</summary>
    public static event Action Check
    {
        add
        {
            _check += value;
            Sync(ref _checks, _check, CheckMs, static () => _check?.Invoke());
        }
        remove
        {
            _check -= value;
            Sync(ref _checks, _check, CheckMs, static () => _check?.Invoke());
        }
    }

    private static void Sync(ref DispatcherTimer? timer, Action? listeners, double intervalMs, Action raise)
    {
        if (listeners is null)
        {
            timer?.Stop();
            return;
        }

        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
            timer.Tick += (_, _) => raise();
        }

        if (!timer.IsEnabled)
        {
            timer.Start();
        }
    }

    /// <summary>How a control's window stands, for deciding how much decoration it is worth.</summary>
    public enum Watching
    {
        /// <summary>In front, being looked at: the embers on every tick, the figure on every frame of the display.</summary>
        InFront,

        /// <summary>On screen behind another window: a few steps a second, as before.</summary>
        Behind,

        /// <summary>Hidden in the tray, minimised, or the game it started is running: nothing moves, nothing is drawn.</summary>
        NotAtAll
    }

    public static Watching Of(Control control)
    {
        if (!control.IsEffectivelyVisible || control.Bounds.Width < 1 || control.Bounds.Height < 1)
        {
            return Watching.NotAtAll;
        }

        if (TopLevel.GetTopLevel(control) is not Window window)
        {
            return Watching.InFront;
        }

        // Hidden to the tray is not minimised: the window keeps its state and only stops being shown.
        if (!window.IsVisible || window.WindowState == WindowState.Minimized)
        {
            return Watching.NotAtAll;
        }

        // The game is running: the player is in the game, and whatever the launcher drew
        // now would be taken from the game's frames. The decoration stands still until it ends.
        if (window.DataContext is MainWindowViewModel { IsGameRunning: true })
        {
            return Watching.NotAtAll;
        }

        return window.IsActive
            ? Watching.InFront
            : Watching.Behind;
    }
}
