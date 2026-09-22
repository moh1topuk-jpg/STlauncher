using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using STlauncher.App.Services;

namespace STlauncher.App.Controls;

/// <summary>
/// The player's face, cut from the skin texture: the 8×8 face plus the hat layer over it,
/// scaled without smoothing so the pixels stay crisp at any size.
/// </summary>
public sealed class SkinFace : Control
{
    public static readonly StyledProperty<PlayerSkin?> SkinProperty =
        AvaloniaProperty.Register<SkinFace, PlayerSkin?>(nameof(Skin));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<SkinFace, CornerRadius>(nameof(CornerRadius), new CornerRadius(8));

    static SkinFace()
    {
        AffectsRender<SkinFace>(SkinProperty, CornerRadiusProperty);
    }

    public SkinFace()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public PlayerSkin? Skin
    {
        get => GetValue(SkinProperty);
        set => SetValue(SkinProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);

        if (Skin is null)
        {
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#221A20")), null, bounds, CornerRadius.TopLeft, CornerRadius.TopLeft);
            return;
        }

        using (context.PushClip(new RoundedRect(bounds, CornerRadius.TopLeft)))
        {
            context.DrawImage(Skin.Texture, new Rect(8, 8, 8, 8), bounds);

            // The hat layer is transparent where there is no hat, so drawing it always is right.
            context.DrawImage(Skin.Texture, new Rect(40, 8, 8, 8), bounds);
        }
    }
}
