using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace STlauncher.App.Controls;

/// <summary>
/// A section caption: the small spaced uppercase line above a list ("WHERE ELSE", "RECENT").
/// Avalonia has no text-transform, and the language files keep their strings in sentence
/// case so the same string can be reused as a heading; this TextBlock uppercases whatever
/// it is given. It is styled as an ordinary TextBlock with the class "caption".
/// </summary>
public sealed class Caption : TextBlock
{
    static Caption()
    {
        TextProperty.OverrideMetadata<Caption>(new StyledPropertyMetadata<string?>(coerce: Upper));
    }

    public Caption()
    {
        Classes.Add("caption");
    }

    // The styles are written for TextBlock; a caption takes them all.
    protected override Type StyleKeyOverride => typeof(TextBlock);

    private static string? Upper(AvaloniaObject sender, string? value)
        => value?.ToUpper(CultureInfo.CurrentUICulture);
}
