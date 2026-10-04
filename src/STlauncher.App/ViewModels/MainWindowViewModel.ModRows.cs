using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace STlauncher.App.ViewModels;

/// <summary>One line of the build's mod list: a card, or two side by side.</summary>
public sealed class ModRow
{
    public ModRow(InstalledModItem left, InstalledModItem? right, bool wide)
    {
        Left = left;
        Right = right;
        LeftSpan = wide ? 3 : 1;
    }

    public InstalledModItem Left { get; }

    public InstalledModItem? Right { get; }

    public bool HasRight => Right is not null;

    /// <summary>Grid columns the left card takes: all three when the list is one card wide.</summary>
    public int LeftSpan { get; }

    /// <summary>The same cards in the same places: such a line needs no redrawing.</summary>
    public bool Shows(ModRow other)
        => ReferenceEquals(Left, other.Left) && ReferenceEquals(Right, other.Right) && LeftSpan == other.LeftSpan;
}

/// <summary>
/// The mod list as the page draws it. The cards are roomy and a build can hold two
/// hundred mods, so the list is virtualised: only the lines on screen exist as controls.
/// A virtualising panel lays out one item per line, hence the pairs; rows the search or
/// the slice hides are simply not in the list, which also keeps the scroll bar honest.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<ModRow> ModRows { get; } = new();

    /// <summary>Cards per line; the page sets it from the width it has.</summary>
    [ObservableProperty]
    private int _modColumns = 2;

    partial void OnModColumnsChanged(int value) => RebuildModRows();

    /// <summary>True when the build has mods but the search or the slice left none on screen.</summary>
    public bool HasNoShownMods => InstalledMods.Count > 0 && ModRows.Count == 0;

    /// <summary>
    /// Lays the visible mods out in lines. Called after every sort and filter, which often
    /// change little on screen - so only the lines that differ are replaced: the list does
    /// not blink and the scroll position stays where the player left it.
    /// </summary>
    private void RebuildModRows()
    {
        var shown = InstalledMods.Where(m => !m.IsHidden).ToList();
        var columns = Math.Clamp(ModColumns, 1, 2);
        var rows = new List<ModRow>((shown.Count + columns - 1) / columns);

        for (var i = 0; i < shown.Count; i += columns)
        {
            rows.Add(new ModRow(
                shown[i],
                columns == 2 && i + 1 < shown.Count ? shown[i + 1] : null,
                wide: columns == 1));
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= ModRows.Count)
            {
                ModRows.Add(rows[i]);
            }
            else if (!ModRows[i].Shows(rows[i]))
            {
                ModRows[i] = rows[i];
            }
        }

        while (ModRows.Count > rows.Count)
        {
            ModRows.RemoveAt(ModRows.Count - 1);
        }

        OnPropertyChanged(nameof(HasNoShownMods));
    }

    /// <summary>The line a file is on, for scrolling to it; -1 when it is not on screen.</summary>
    public int ModRowIndexOf(string fileName)
    {
        for (var i = 0; i < ModRows.Count; i++)
        {
            if (string.Equals(ModRows[i].Left.FileName, fileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ModRows[i].Right?.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
