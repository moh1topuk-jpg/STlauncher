using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;

namespace STlauncher.App.Controls;

/// <summary>
/// A Flyout whose panel fades and slides into place on every opening. Avalonia's own
/// Flyout just appears: the popup has no entrance of its own, and the presenter is
/// created once and reused, so a style animation would play only the first time. This
/// presenter plays the reveal each time it is attached, which is each time it opens.
/// </summary>
public sealed class RevealFlyout : Flyout
{
    protected override Control CreatePresenter()
    {
        var presenter = new RevealFlyoutPresenter();
        presenter[!ContentControl.ContentProperty] = this[!ContentProperty];
        return presenter;
    }
}

public sealed class RevealFlyoutPresenter : FlyoutPresenter
{
    /// <summary>Keeps the FlyoutPresenter template and styles; only the behaviour differs.</summary>
    protected override Type StyleKeyOverride => typeof(FlyoutPresenter);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Reveal.Play(this, offset: 10);
    }
}
