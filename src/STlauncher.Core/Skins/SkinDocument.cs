using System;
using System.Collections.Generic;

namespace STlauncher.Core.Skins;

public enum SkinTool
{
    Pencil,
    Eraser,
    Fill,
    Picker
}

/// <summary>
/// A skin being edited: the pixels, the tools that change them, and the history that
/// takes each change back.
/// </summary>
/// <remarks>
/// A stroke is everything between pressing and releasing the button, and it is undone
/// as one step. Only the pixels that really changed are remembered, with what they were
/// and what they became, so a hundred steps of history cost a few kilobytes.
/// </remarks>
public sealed class SkinDocument
{
    /// <summary>Steps kept. Older ones fall off the far end.</summary>
    public const int HistoryLimit = 200;

    private readonly record struct Change(int Index, uint Before, uint After);

    private sealed record Step(long Id, Change[] Changes);

    private readonly List<Step> _undo = new();
    private readonly List<Step> _redo = new();
    private Dictionary<int, uint>? _stroke;
    private long _nextId = 1;
    private long _baseId;
    private long _savedId;
    private SkinModel _savedModel;
    private SkinModel _model;

    public SkinDocument(SkinImage image, SkinModel model)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        _model = model;
        _savedModel = model;
    }

    public SkinImage Image { get; }

    /// <summary>Raised after the pixels or the model changed.</summary>
    public event Action? Changed;

    public SkinModel Model
    {
        get => _model;
        set
        {
            if (_model != value)
            {
                _model = value;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Whatever is drawn on one half of the body is drawn on the other as well.</summary>
    public bool Mirror { get; set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>True when what is on screen differs from what was last saved.</summary>
    public bool IsDirty => CurrentId != _savedId || _model != _savedModel;

    private long CurrentId => _undo.Count > 0 ? _undo[^1].Id : _baseId;

    public void MarkSaved()
    {
        _savedId = CurrentId;
        _savedModel = _model;
    }

    public void BeginStroke()
    {
        EndStroke();
        _stroke = new Dictionary<int, uint>();
    }

    /// <summary>
    /// Uses a tool on one pixel. The pipette changes nothing and returns the colour
    /// under it; the others return null. Outside a stroke the change is a step of its own.
    /// </summary>
    public uint? Apply(SkinTool tool, int x, int y, uint colour)
    {
        if (!SkinImage.Contains(x, y))
        {
            return null;
        }

        if (tool == SkinTool.Picker)
        {
            return Image[x, y];
        }

        var single = _stroke is null;

        if (single)
        {
            _stroke = new Dictionary<int, uint>();
        }

        var changed = Touch(tool, x, y, colour);

        if (Mirror && SkinLayout.Mirror(x, y, _model) is { } twin)
        {
            changed |= Touch(tool, twin.X, twin.Y, colour);
        }

        if (single)
        {
            EndStroke();
        }
        else if (changed)
        {
            Changed?.Invoke();
        }

        return null;
    }

    /// <summary>Closes the stroke; if it changed anything, it becomes one step of history.</summary>
    public void EndStroke()
    {
        var stroke = _stroke;
        _stroke = null;

        if (stroke is null)
        {
            return;
        }

        var changes = new List<Change>(stroke.Count);

        foreach (var (index, before) in stroke)
        {
            // A pixel painted and painted back within one stroke is not a change.
            if (Image[index] != before)
            {
                changes.Add(new Change(index, before, Image[index]));
            }
        }

        if (changes.Count == 0)
        {
            return;
        }

        _undo.Add(new Step(_nextId++, changes.ToArray()));
        _redo.Clear();

        if (_undo.Count > HistoryLimit)
        {
            // What lies under the history is now the state after the forgotten step.
            _baseId = _undo[0].Id;
            _undo.RemoveAt(0);
        }

        Changed?.Invoke();
    }

    public bool Undo()
    {
        EndStroke();

        if (_undo.Count == 0)
        {
            return false;
        }

        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        foreach (var change in step.Changes)
        {
            Image[change.Index] = change.Before;
        }

        _redo.Add(step);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        EndStroke();

        if (_redo.Count == 0)
        {
            return false;
        }

        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        foreach (var change in step.Changes)
        {
            Image[change.Index] = change.After;
        }

        _undo.Add(step);
        Changed?.Invoke();
        return true;
    }

    private bool Touch(SkinTool tool, int x, int y, uint colour) => tool switch
    {
        SkinTool.Pencil => Set(x, y, colour),
        SkinTool.Eraser => Set(x, y, 0),
        SkinTool.Fill => Fill(x, y, colour),
        _ => false
    };

    private bool Set(int x, int y, uint colour)
    {
        var index = y * SkinImage.Size + x;
        var before = Image[index];

        if (before == colour)
        {
            return false;
        }

        _stroke!.TryAdd(index, before);
        Image[index] = colour;
        return true;
    }

    /// <summary>
    /// Floods the touching pixels of the same colour, and stops at the edge of the face:
    /// neighbours in the texture are not neighbours on the body, and a fill that ran
    /// from the face of the head onto its top would only ever be a surprise.
    /// </summary>
    private bool Fill(int x, int y, uint colour)
    {
        if (SkinLayout.FaceAt(x, y, _model) is not { } face)
        {
            return false;
        }

        var target = Image[x, y];

        if (target == colour)
        {
            return false;
        }

        var pending = new Stack<(int X, int Y)>();
        pending.Push((x, y));

        while (pending.Count > 0)
        {
            var (px, py) = pending.Pop();

            if (!face.Rect.Contains(px, py) || Image[px, py] != target)
            {
                continue;
            }

            Set(px, py, colour);
            pending.Push((px + 1, py));
            pending.Push((px - 1, py));
            pending.Push((px, py + 1));
            pending.Push((px, py - 1));
        }

        return true;
    }
}
