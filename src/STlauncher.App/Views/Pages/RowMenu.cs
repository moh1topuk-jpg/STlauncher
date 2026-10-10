using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace STlauncher.App.Views.Pages;

/// <summary>
/// The "..." button of a row opens the row's own right-click menu, under the button. One
/// menu serves the mouse, the keyboard and a finger, and it is written once: a rare
/// action can never be in one of the two menus and missing from the other.
/// </summary>
internal static class RowMenu
{
    public static void OpenFrom(object? sender)
    {
        if (sender is not Button button)
        {
            return;
        }

        var owner = button.GetVisualAncestors().OfType<Control>().FirstOrDefault(c => c.ContextMenu is not null);

        if (owner?.ContextMenu is not { } menu)
        {
            return;
        }

        menu.Placement = PlacementMode.BottomEdgeAlignedRight;
        menu.PlacementTarget = button;

        // A right click afterwards opens the menu where the pointer is, as before.
        void Reset(object? s, RoutedEventArgs e)
        {
            menu.Closed -= Reset;
            menu.Placement = PlacementMode.Pointer;
            menu.PlacementTarget = null;
        }

        menu.Closed += Reset;
        menu.Open(owner);
    }
}
