using System;
using System.Linq;

namespace STlauncher.App.ViewModels;

/// <summary>
/// What the launcher holds on to and when it lets go. Pictures are most of its memory -
/// mod logos, gallery previews, screenshot thumbnails, the enlarged textures of the skin
/// figures - and each kind is only worth keeping while its page is on screen. The launcher
/// stays open, or sits in the tray, for as long as the game runs: on a weak machine every
/// megabyte it keeps there is one the game does not have.
/// </summary>
public partial class MainWindowViewModel
{
    partial void OnSectionChanged(ShellSection oldValue, ShellSection newValue)
        => OnScreenChanged(oldValue, BuildTab, newValue, BuildTab);

    partial void OnBuildTabChanged(BuildTab oldValue, BuildTab newValue)
        => OnScreenChanged(Section, oldValue, Section, newValue);

    /// <summary>One place that knows which page was left and which was opened, whether by the rail or by a tab.</summary>
    private void OnScreenChanged(ShellSection fromSection, BuildTab fromTab, ShellSection toSection, BuildTab toTab)
    {
        bool Was(BuildTab tab) => fromSection == ShellSection.Builds && fromTab == tab;
        bool Is(BuildTab tab) => toSection == ShellSection.Builds && toTab == tab;

        // The catalog is the build's tab and the Mods section alike: going from one to the
        // other keeps what was found.
        var wasCatalog = IsCatalogScreen(fromSection, fromTab);
        var isCatalog = IsCatalogScreen(toSection, toTab);

        if (wasCatalog && !isCatalog)
        {
            // The cards keep the logos they show; everything else found while browsing goes.
            _images.Clear();
        }

        if (isCatalog && !wasCatalog)
        {
            OnCatalogShown();

            // A mirror that could not be reached at start gets one more question here too.
            if (toSection == ShellSection.Mods && !IsCurseForgeAvailable)
            {
                _ = ProbeCurseForgeAsync();
            }
        }

        if (Was(BuildTab.Screenshots) && !Is(BuildTab.Screenshots))
        {
            PauseScreenshots();
        }

        // Back to the builds page with the screenshots tab still the open one: the tab
        // itself did not change, so nothing else would pick the decoding up again.
        if (Is(BuildTab.Screenshots) && !Was(BuildTab.Screenshots) && fromTab == toTab)
        {
            RefreshScreenshots();
        }

        if (fromSection == ShellSection.Skins && toSection != ShellSection.Skins)
        {
            ReleaseSkinFigures(includeWorn: false);
        }
    }

    /// <summary>
    /// Frees the enlarged texture of every skin figure: a megabyte each, for a dozen cards
    /// and the stages beside them. Each is made again when its figure is next drawn.
    /// </summary>
    /// <param name="includeWorn">Also the skin on the home screen's figure - for when no page is on screen at all.</param>
    private void ReleaseSkinFigures(bool includeWorn)
    {
        foreach (var skin in LibrarySkins.Select(s => s.Skin)
                     .Concat(RandomSkins.Select(s => s.Skin))
                     .Append(EditorPreviewSkin)
                     .Append(includeWorn ? PlayerSkin : null))
        {
            if (skin is not null && (includeWorn || !ReferenceEquals(skin, PlayerSkin)))
            {
                skin.ReleaseEnlarged();
            }
        }
    }

    /// <summary>True between hiding for the game and coming back.</summary>
    private bool _releasedForGame;

    /// <summary>
    /// The window has just been hidden because the game started. Everything that is only
    /// there to be looked at is let go of, and what must not be lost is put on disk: the
    /// launcher may not be seen again for hours, or may be closed along with the game.
    /// </summary>
    public void ReleaseMemoryForGame()
    {
        FlushSettings();
        FlushLauncherLog();

        _releasedForGame = true;
        _images.Clear();
        ReleaseScreenshotThumbnails();
        ReleaseSkinFigures(includeWorn: true);
    }

    /// <summary>A line for the console and the log from outside the view model.</summary>
    public void Note(string line) => AppendConsole(line);

    /// <summary>
    /// The launcher is closing: a settings save still waiting and the last lines of the
    /// log are written before the process goes. Called from the interface thread on a
    /// normal exit and from the runtime's own exit hook otherwise, so it touches nothing
    /// that belongs to the interface.
    /// </summary>
    public void FlushOnExit()
    {
        try
        {
            _settings.Flush();
        }
        catch (Exception)
        {
            // Nobody is left to tell.
        }

        try
        {
            while (_pendingConsoleLines.TryDequeue(out var line))
            {
                _launcherLogLines.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
            }

            FlushLauncherLogFromAnyThread();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>The window is back: whatever page it is on gets back what was let go of.</summary>
    public void RestoreAfterGame()
    {
        if (!_releasedForGame)
        {
            return;
        }

        _releasedForGame = false;

        if (IsScreenshotsTabOnScreen)
        {
            RefreshScreenshots();
        }
    }
}
