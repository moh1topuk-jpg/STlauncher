using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using STlauncher.Core.Diagnostics;

namespace STlauncher.App.Controls;

/// <summary>
/// Streamer mode, for the pages. A page does not decide what to hide: it says what a
/// TextBlock shows and leaves the hiding to this class, so the mode can be switched on and
/// off while every page is on screen.
/// <code>
///   &lt;TextBlock controls:Streamer.Text="{Binding Note}"/&gt;      any line: nickname, IPs, codes, paths
///   &lt;TextBlock controls:Streamer.Address="{Binding Address}"/&gt; an address, dots unless it is the public server
///   &lt;TextBlock controls:Streamer.Code="{Binding Code}" controls:Streamer.RevealKey="invite"/&gt;
///                                                              a code; the eye button shows the one with that key for 5 s
///   &lt;TextBox controls:Streamer.Field="True"/&gt;                  dots until the field has the focus
/// </code>
/// The view model sets the state (<see cref="Configure"/>); with the mode off every value
/// is shown as it is and nothing is computed.
/// </summary>
public static class Streamer
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(Streamer));

    public static readonly AttachedProperty<string?> AddressProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Address", typeof(Streamer));

    public static readonly AttachedProperty<string?> CodeProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Code", typeof(Streamer));

    /// <summary>Which reveal button this code answers to.</summary>
    public static readonly AttachedProperty<string?> RevealKeyProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("RevealKey", typeof(Streamer));

    public static readonly AttachedProperty<bool> FieldProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Field", typeof(Streamer));

    private const char FieldDot = '•';

    private static StreamerRedactionContext _context = new(null, null, null, "Player");

    /// <summary>The controls on screen that show something through this class.</summary>
    private static readonly HashSet<Control> Live = new();

    /// <summary>The controls whose events are already listened to; weak, so a line of a long log is not kept alive.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Hooked = new();

    static Streamer()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((c, _) => Track(c));
        AddressProperty.Changed.AddClassHandler<TextBlock>((c, _) => Track(c));
        CodeProperty.Changed.AddClassHandler<TextBlock>((c, _) => Track(c));
        RevealKeyProperty.Changed.AddClassHandler<TextBlock>((c, _) => Track(c));
        FieldProperty.Changed.AddClassHandler<TextBox>((c, _) => Track(c));
    }

    public static bool IsOn { get; private set; }

    /// <summary>The code that is shown in full for a moment, by its key.</summary>
    public static string? RevealedKey { get; private set; }

    private static int _revealPress;

    /// <summary>Sets the mode and what it hides; everything on screen is shown again at once.</summary>
    public static void Configure(bool on, StreamerRedactionContext context)
    {
        var wasOn = IsOn;
        IsOn = on;
        _context = context ?? throw new ArgumentNullException(nameof(context));

        // Off before and after: what is on screen is the plain text already.
        if (!on && !wasOn)
        {
            return;
        }

        if (!on)
        {
            RevealedKey = null;
            _revealPress++;
        }

        Refresh();
    }

    /// <summary>Shows the code with this key in full for <paramref name="duration"/>, then hides it again.</summary>
    public static void Reveal(string? key, TimeSpan duration)
    {
        if (!IsOn || string.IsNullOrEmpty(key))
        {
            return;
        }

        RevealedKey = key;
        Refresh();

        // A second press starts the five seconds again; the earlier wait then ends doing nothing.
        var press = ++_revealPress;

        _ = System.Threading.Tasks.Task.Delay(duration).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (press == _revealPress && RevealedKey is not null)
            {
                RevealedKey = null;
                Refresh();
            }
        }));
    }

    // ===================== What is shown =====================

    public static string ShowText(string? text)
        => StreamerRedactor.Show(text, IsOn, _context);

    public static string ShowAddress(string? address)
        => IsOn ? StreamerRedactor.MaskAddress(address, _context.PublicServers) : address ?? string.Empty;

    public static string ShowCode(string? code, string? key)
        => IsOn && (key is null || !string.Equals(key, RevealedKey, StringComparison.Ordinal))
            ? StreamerRedactor.MaskCode(code)
            : code ?? string.Empty;

    // ===================== Attached properties =====================

    public static string? GetText(TextBlock c) => c.GetValue(TextProperty);
    public static void SetText(TextBlock c, string? value) => c.SetValue(TextProperty, value);

    public static string? GetAddress(TextBlock c) => c.GetValue(AddressProperty);
    public static void SetAddress(TextBlock c, string? value) => c.SetValue(AddressProperty, value);

    public static string? GetCode(TextBlock c) => c.GetValue(CodeProperty);
    public static void SetCode(TextBlock c, string? value) => c.SetValue(CodeProperty, value);

    public static string? GetRevealKey(TextBlock c) => c.GetValue(RevealKeyProperty);
    public static void SetRevealKey(TextBlock c, string? value) => c.SetValue(RevealKeyProperty, value);

    public static bool GetField(TextBox c) => c.GetValue(FieldProperty);
    public static void SetField(TextBox c, bool value) => c.SetValue(FieldProperty, value);

    private static void Track(Control control)
    {
        if (!Hooked.TryGetValue(control, out _))
        {
            Hooked.Add(control, Hooked);

            control.AttachedToVisualTree += (_, _) =>
            {
                Live.Add(control);
                Update(control);
            };
            control.DetachedFromVisualTree += (_, _) => Live.Remove(control);

            if (control is TextBox box)
            {
                box.GotFocus += (_, _) => Update(box);
                box.LostFocus += (_, _) => Update(box);
            }
        }

        // Kept off the list until it is on screen: a template that never shows costs nothing.
        if (control.IsAttachedToVisualTree())
        {
            Live.Add(control);
        }

        Update(control);
    }

    private static void Refresh()
    {
        foreach (var control in Live.ToList())
        {
            Update(control);
        }
    }

    private static void Update(Control control)
    {
        switch (control)
        {
            case TextBox box when box.GetValue(FieldProperty):
                box.PasswordChar = IsOn ? FieldDot : default;
                box.RevealPassword = IsOn && box.IsFocused;
                break;

            case TextBlock block when block.IsSet(CodeProperty):
                block.Text = ShowCode(block.GetValue(CodeProperty), block.GetValue(RevealKeyProperty));
                break;

            case TextBlock block when block.IsSet(AddressProperty):
                block.Text = ShowAddress(block.GetValue(AddressProperty));
                break;

            case TextBlock block when block.IsSet(TextProperty):
                block.Text = ShowText(block.GetValue(TextProperty));
                break;
        }
    }

    private static bool IsAttachedToVisualTree(this Visual visual)
        => Avalonia.VisualTree.VisualExtensions.GetVisualRoot(visual) is not null;
}
