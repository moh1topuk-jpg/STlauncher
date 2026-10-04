using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using STlauncher.App.Services;
using STlauncher.App.ViewModels;
using STlauncher.Core.Skins;

namespace STlauncher.App.Controls;

/// <summary>
/// The skin editor's drawing surface: the texture taken apart by body part, each part
/// unfolded and labelled, every texture pixel a square large enough to aim at.
/// </summary>
/// <remarks>
/// The raw 64×64 texture is a puzzle: the left arm sits under the body, the second
/// layer of the legs is scattered over three corners. Here each part gets its own
/// block, and one layer is shown at a time, so "the jacket" is where "the body" was a
/// click ago. Nothing is animated: the surface is redrawn when a pixel changes or the
/// pointer moves to another square, and at no other time.
/// </remarks>
public sealed class SkinCanvas : Control
{
    public static readonly StyledProperty<SkinDocument?> DocumentProperty =
        AvaloniaProperty.Register<SkinCanvas, SkinDocument?>(nameof(Document));

    public static readonly StyledProperty<SkinLayer> LayerProperty =
        AvaloniaProperty.Register<SkinCanvas, SkinLayer>(nameof(Layer));

    public static readonly StyledProperty<SkinTool> ToolProperty =
        AvaloniaProperty.Register<SkinCanvas, SkinTool>(nameof(Tool));

    public static readonly StyledProperty<Color> ColourProperty =
        AvaloniaProperty.Register<SkinCanvas, Color>(nameof(Colour), Colors.Black, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<SkinCanvas, bool>(nameof(ShowGrid), true);

    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<SkinCanvas, IBrush?>(nameof(LabelBrush));

    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<SkinCanvas, IBrush?>(nameof(OutlineBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<SkinCanvas, IBrush?>(nameof(GridBrush));

    /// <summary>The two tones of the chequered paper that shows through transparent pixels.</summary>
    public static readonly StyledProperty<IBrush?> PaperBrushProperty =
        AvaloniaProperty.Register<SkinCanvas, IBrush?>(nameof(PaperBrush));

    public static readonly StyledProperty<IBrush?> PaperAltBrushProperty =
        AvaloniaProperty.Register<SkinCanvas, IBrush?>(nameof(PaperAltBrush));

    public static readonly DirectProperty<SkinCanvas, string> HoverTextProperty =
        AvaloniaProperty.RegisterDirect<SkinCanvas, string>(nameof(HoverText), canvas => canvas.HoverText);

    /// <summary>Room above each block for its name, and between the rows, in screen pixels.</summary>
    private const double LabelHeight = 20;

    private const double RowGap = 10;

    /// <summary>Cells between two blocks in a row.</summary>
    private const int BlockGap = 2;

    /// <summary>Every part is given the room of the widest variant, so switching the model moves nothing.</summary>
    private const int LimbSlot = 16;

    private static readonly SkinSide[] Sides = Enum.GetValues<SkinSide>();

    /// <summary>Two rows for a wide surface, three for a tall one; whichever gives larger squares is used.</summary>
    private static readonly SkinPart[][][] Arrangements =
    {
        new[]
        {
            new[] { SkinPart.Head, SkinPart.Body },
            new[] { SkinPart.RightArm, SkinPart.LeftArm, SkinPart.RightLeg, SkinPart.LeftLeg }
        },
        new[]
        {
            new[] { SkinPart.Head, SkinPart.Body },
            new[] { SkinPart.RightArm, SkinPart.LeftArm },
            new[] { SkinPart.RightLeg, SkinPart.LeftLeg }
        }
    };

    /// <summary>One side of one part as placed on the surface, with the texture it shows.</summary>
    private readonly record struct Placed(SkinPatch Face, SkinRect Under, Rect Bounds);

    private readonly record struct Caption(SkinPart Part, Point Origin);

    private readonly List<Placed> _faces = new();
    private readonly List<Caption> _captions = new();
    private double _cell;
    private Size _arrangedFor;
    private SkinModel _arrangedModel;
    private SkinLayer _arrangedLayer;

    private SkinDocument? _subscribed;
    private Bitmap? _bitmap;
    private Bitmap? _paper;
    private Color _paperColour;
    private Color _paperAltColour;

    private (int X, int Y)? _hover;
    private (int X, int Y)? _lastDrawn;
    private Point _lastPoint;
    private bool _drawing;
    private string _hoverText = string.Empty;

    static SkinCanvas()
    {
        AffectsRender<SkinCanvas>(
            DocumentProperty, LayerProperty, ShowGridProperty,
            LabelBrushProperty, OutlineBrushProperty, GridBrushProperty, PaperBrushProperty, PaperAltBrushProperty);
        FocusableProperty.OverrideDefaultValue<SkinCanvas>(true);
    }

    public SkinCanvas()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        Cursor = new Cursor(StandardCursorType.Cross);
        ClipToBounds = true;

        DetachedFromVisualTree += (_, _) => Subscribe(null);
        AttachedToVisualTree += (_, _) => Subscribe(Document);
    }

    /// <summary>Raised when the pipette took a colour, so the editor can hand the pencil back.</summary>
    public event EventHandler? ColourPicked;

    public SkinDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public SkinLayer Layer
    {
        get => GetValue(LayerProperty);
        set => SetValue(LayerProperty, value);
    }

    public SkinTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    public Color Colour
    {
        get => GetValue(ColourProperty);
        set => SetValue(ColourProperty, value);
    }

    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public IBrush? LabelBrush
    {
        get => GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public IBrush? OutlineBrush
    {
        get => GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? PaperBrush
    {
        get => GetValue(PaperBrushProperty);
        set => SetValue(PaperBrushProperty, value);
    }

    public IBrush? PaperAltBrush
    {
        get => GetValue(PaperAltBrushProperty);
        set => SetValue(PaperAltBrushProperty, value);
    }

    /// <summary>What is under the pointer, in words: "Head · front".</summary>
    public string HoverText
    {
        get => _hoverText;
        private set => SetAndRaise(HoverTextProperty, ref _hoverText, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DocumentProperty)
        {
            Subscribe(this.IsAttachedToVisualTree() ? Document : null);
        }
    }

    private void Subscribe(SkinDocument? document)
    {
        if (ReferenceEquals(_subscribed, document))
        {
            return;
        }

        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnDocumentChanged;
        }

        _subscribed = document;

        if (document is not null)
        {
            document.Changed += OnDocumentChanged;
        }

        OnDocumentChanged();
    }

    private void OnDocumentChanged()
    {
        _bitmap?.Dispose();
        _bitmap = _subscribed is null ? null : SkinPng.ToBitmap(_subscribed.Image);
        InvalidateVisual();
    }

    // ===================== Where everything goes =====================

    private void Arrange(SkinModel model)
    {
        if (_arrangedFor == Bounds.Size && _arrangedModel == model && _arrangedLayer == Layer && _faces.Count > 0)
        {
            return;
        }

        _arrangedFor = Bounds.Size;
        _arrangedModel = model;
        _arrangedLayer = Layer;
        _faces.Clear();
        _captions.Clear();

        var best = Arrangements[0];
        _cell = 0;

        foreach (var arrangement in Arrangements)
        {
            var cell = CellFor(arrangement);

            if (cell > _cell)
            {
                _cell = cell;
                best = arrangement;
            }
        }

        if (_cell < 2)
        {
            return;
        }

        var height = best.Length * (LabelHeight + 16 * _cell) + (best.Length - 1) * RowGap;
        var top = Math.Max(0, Math.Floor((Bounds.Height - height) / 2));
        var width = WidthInCells(best) * _cell;
        var left = Math.Max(0, Math.Floor((Bounds.Width - width) / 2));

        foreach (var row in best)
        {
            var x = left;

            foreach (var part in row)
            {
                _captions.Add(new Caption(part, new Point(x, top)));

                foreach (var side in Sides)
                {
                    var local = SkinLayout.LocalRect(part, side, model);
                    var bounds = new Rect(
                        x + local.X * _cell,
                        top + LabelHeight + local.Y * _cell,
                        local.Width * _cell,
                        local.Height * _cell);

                    _faces.Add(new Placed(
                        SkinLayout.Face(part, Layer, side, model),
                        SkinLayout.Face(part, SkinLayer.Base, side, model).Rect,
                        bounds));
                }

                x += (Slot(part) + BlockGap) * _cell;
            }

            top += LabelHeight + 16 * _cell + RowGap;
        }
    }

    private static int Slot(SkinPart part) => part switch
    {
        SkinPart.Head => 32,
        SkinPart.Body => 24,
        _ => LimbSlot
    };

    private static int WidthInCells(SkinPart[][] arrangement)
    {
        var widest = 0;

        foreach (var row in arrangement)
        {
            var width = -BlockGap;

            foreach (var part in row)
            {
                width += Slot(part) + BlockGap;
            }

            widest = Math.Max(widest, width);
        }

        return widest;
    }

    /// <summary>The largest whole square that lets this arrangement fit; whole, so every pixel is crisp.</summary>
    private double CellFor(SkinPart[][] arrangement)
    {
        var rows = arrangement.Length;
        var across = Bounds.Width / WidthInCells(arrangement);
        var down = (Bounds.Height - rows * LabelHeight - (rows - 1) * RowGap) / (rows * 16);
        return Math.Floor(Math.Min(across, down));
    }

    private (int X, int Y)? PixelAt(Point point)
    {
        foreach (var placed in _faces)
        {
            if (placed.Bounds.Contains(point))
            {
                var x = (int)((point.X - placed.Bounds.X) / _cell);
                var y = (int)((point.Y - placed.Bounds.Y) / _cell);
                var rect = placed.Face.Rect;
                return (rect.X + Math.Min(x, rect.Width - 1), rect.Y + Math.Min(y, rect.Height - 1));
            }
        }

        return null;
    }

    private Rect? CellBounds((int X, int Y) pixel)
    {
        foreach (var placed in _faces)
        {
            if (placed.Face.Rect.Contains(pixel.X, pixel.Y))
            {
                return new Rect(
                    placed.Bounds.X + (pixel.X - placed.Face.Rect.X) * _cell,
                    placed.Bounds.Y + (pixel.Y - placed.Face.Rect.Y) * _cell,
                    _cell,
                    _cell);
            }
        }

        return null;
    }

    // ===================== Drawing with the pointer =====================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (Document is not { } document)
        {
            return;
        }

        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;

        if (PixelAt(point) is not { } pixel)
        {
            return;
        }

        // The right button is always the pipette: taking a colour off the skin is too
        // frequent to make the player change tools for it.
        if (properties.IsRightButtonPressed || Tool == SkinTool.Picker)
        {
            Pick(document, pixel);
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        document.BeginStroke();
        document.Apply(Tool, pixel.X, pixel.Y, Colour.ToUInt32());

        if (Tool == SkinTool.Fill)
        {
            document.EndStroke();
        }
        else
        {
            _drawing = true;
            _lastDrawn = pixel;
            _lastPoint = point;
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = e.GetPosition(this);
        var pixel = PixelAt(point);

        if (_drawing && Document is { } document)
        {
            // A fast hand skips squares between two reports of the pointer; walking the
            // line between them in half-square steps leaves no gaps in the stroke.
            var distance = Math.Sqrt(Math.Pow(point.X - _lastPoint.X, 2) + Math.Pow(point.Y - _lastPoint.Y, 2));
            var steps = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(1, _cell / 2)));

            for (var i = 1; i <= steps; i++)
            {
                var between = new Point(
                    _lastPoint.X + (point.X - _lastPoint.X) * i / steps,
                    _lastPoint.Y + (point.Y - _lastPoint.Y) * i / steps);

                if (PixelAt(between) is { } on && on != _lastDrawn)
                {
                    document.Apply(Tool, on.X, on.Y, Colour.ToUInt32());
                    _lastDrawn = on;
                }
            }

            _lastPoint = point;
        }

        SetHover(pixel);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_drawing)
        {
            _drawing = false;
            _lastDrawn = null;
            e.Pointer.Capture(null);
            Document?.EndStroke();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);

        if (_drawing)
        {
            _drawing = false;
            _lastDrawn = null;
            Document?.EndStroke();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
    }

    private void Pick(SkinDocument document, (int X, int Y) pixel)
    {
        // An empty pixel has no colour to take.
        if (document.Apply(SkinTool.Picker, pixel.X, pixel.Y, 0) is { } picked && picked >> 24 != 0)
        {
            SetCurrentValue(ColourProperty, Color.FromUInt32(picked | 0xFF000000));
            ColourPicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetHover((int X, int Y)? pixel)
    {
        if (_hover == pixel)
        {
            return;
        }

        _hover = pixel;

        HoverText = pixel is { } at && Document is { } document && SkinLayout.FaceAt(at.X, at.Y, document.Model) is { } face
            ? PartName(face.Part) + " · " + SideName(face.Side)
            : string.Empty;

        InvalidateVisual();
    }

    private static string PartName(SkinPart part) => part switch
    {
        SkinPart.Head => MainWindowViewModel.Localize("Skins_PartHead", "Head"),
        SkinPart.Body => MainWindowViewModel.Localize("Skins_PartBody", "Body"),
        SkinPart.RightArm => MainWindowViewModel.Localize("Skins_PartRightArm", "Right arm"),
        SkinPart.LeftArm => MainWindowViewModel.Localize("Skins_PartLeftArm", "Left arm"),
        SkinPart.RightLeg => MainWindowViewModel.Localize("Skins_PartRightLeg", "Right leg"),
        _ => MainWindowViewModel.Localize("Skins_PartLeftLeg", "Left leg")
    };

    private static string SideName(SkinSide side) => side switch
    {
        SkinSide.Top => MainWindowViewModel.Localize("Skins_SideTop", "top"),
        SkinSide.Bottom => MainWindowViewModel.Localize("Skins_SideBottom", "bottom"),
        SkinSide.Right => MainWindowViewModel.Localize("Skins_SideRight", "right side"),
        SkinSide.Front => MainWindowViewModel.Localize("Skins_SideFront", "front"),
        SkinSide.Left => MainWindowViewModel.Localize("Skins_SideLeft", "left side"),
        _ => MainWindowViewModel.Localize("Skins_SideBack", "back")
    };

    // ===================== Painting the surface =====================

    public override void Render(DrawingContext context)
    {
        // Something to hit even where nothing is drawn, so the cursor stays a cross.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));

        if (Document is not { } document || _bitmap is null)
        {
            return;
        }

        Arrange(document.Model);

        if (_cell < 2)
        {
            return;
        }

        var paper = Paper();
        var outline = OutlineBrush is { } outlineBrush ? new Pen(outlineBrush, 1) : null;
        var grid = ShowGrid && _cell >= 5 && GridBrush is { } gridBrush ? new Pen(gridBrush, 1) : null;
        var typeface = new Typeface(GetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty));

        foreach (var caption in _captions)
        {
            var text = new FormattedText(
                PartName(caption.Part), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, 11.5, LabelBrush ?? Brushes.Gray);

            context.DrawText(text, new Point(caption.Origin.X, caption.Origin.Y + 2));
        }

        foreach (var placed in _faces)
        {
            var rect = placed.Face.Rect;
            var source = new Rect(rect.X, rect.Y, rect.Width, rect.Height);

            if (paper is not null)
            {
                context.DrawImage(paper, new Rect(0, 0, rect.Width, rect.Height), placed.Bounds);
            }

            // Under the second layer, the skin it is worn over: faint, so it is clear
            // which of the two is being drawn on.
            if (placed.Face.Layer == SkinLayer.Overlay)
            {
                using (context.PushOpacity(0.4))
                {
                    context.DrawImage(_bitmap, new Rect(placed.Under.X, placed.Under.Y, placed.Under.Width, placed.Under.Height), placed.Bounds);
                }
            }

            context.DrawImage(_bitmap, source, placed.Bounds);

            if (grid is not null)
            {
                for (var column = 1; column < rect.Width; column++)
                {
                    var x = Math.Round(placed.Bounds.X + column * _cell) + 0.5;
                    context.DrawLine(grid, new Point(x, placed.Bounds.Y), new Point(x, placed.Bounds.Bottom));
                }

                for (var row = 1; row < rect.Height; row++)
                {
                    var y = Math.Round(placed.Bounds.Y + row * _cell) + 0.5;
                    context.DrawLine(grid, new Point(placed.Bounds.X, y), new Point(placed.Bounds.Right, y));
                }
            }

            if (outline is not null)
            {
                context.DrawRectangle(null, outline, placed.Bounds.Inflate(0.5));
            }
        }

        if (_hover is { } hover)
        {
            DrawMarker(context, hover, 1);

            if (document.Mirror && SkinLayout.Mirror(hover.X, hover.Y, document.Model) is { } twin && twin != hover)
            {
                DrawMarker(context, twin, 0.55);
            }
        }
    }

    /// <summary>A dark ring inside a light one: visible on any colour the square may have.</summary>
    private void DrawMarker(DrawingContext context, (int X, int Y) pixel, double opacity)
    {
        if (CellBounds(pixel) is not { } bounds)
        {
            return;
        }

        using (context.PushOpacity(opacity))
        {
            context.DrawRectangle(null, new Pen(Brushes.Black, 1), bounds.Deflate(0.5));
            context.DrawRectangle(null, new Pen(Brushes.White, 1), bounds.Inflate(0.5));
        }
    }

    /// <summary>The chequered paper as a tiny bitmap, remade only when the theme changes its tones.</summary>
    private Bitmap? Paper()
    {
        if (PaperBrush is not ISolidColorBrush first || PaperAltBrush is not ISolidColorBrush second)
        {
            return null;
        }

        if (_paper is not null && _paperColour == first.Color && _paperAltColour == second.Color)
        {
            return _paper;
        }

        _paperColour = first.Color;
        _paperAltColour = second.Color;

        const int size = 32;
        var pixels = new uint[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[y * size + x] = ((x + y) % 2 == 0 ? first.Color : second.Color).ToUInt32() | 0xFF000000;
            }
        }

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);

        try
        {
            _paper?.Dispose();
            _paper = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(size, size), new Vector(96, 96), size * 4);
        }
        finally
        {
            handle.Free();
        }

        return _paper;
    }
}
