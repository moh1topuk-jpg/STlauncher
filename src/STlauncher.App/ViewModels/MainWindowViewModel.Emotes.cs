using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using STlauncher.App.Controls;
using STlauncher.Core.Emotes;

namespace STlauncher.App.ViewModels;

/// <summary>
/// What the figure on the home screen can be asked to do: its own poses, and the
/// Emotecraft emotes of the build being played - the files in the build's emotes folder
/// and the ones inside the mod's jar. Only read, never fetched: a build without the mod
/// simply has none.
/// </summary>
public partial class MainWindowViewModel
{
    private readonly EmoteLibrary _emoteLibrary = new();
    private bool _watchingEmotes;
    private int _emoteLoad;

    /// <summary>The figure's own poses, named in the interface language.</summary>
    public ObservableCollection<PoseChoice> HomePoses { get; } = new();

    /// <summary>The selected build's emotes, the player's own files first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHomeEmotes))]
    private IReadOnlyList<Emote> _homeEmotes = Array.Empty<Emote>();

    public bool HasHomeEmotes => HomeEmotes.Count > 0;

    /// <summary>
    /// Called by the home screen once it has a view model: from then on the list follows
    /// the selected build and the language. Nothing is read before a screen wants it.
    /// </summary>
    /// <summary>
    /// Emotecraft emotes are off until the figure plays them the way the game does: its
    /// pose math follows the old playerAnimator, while the mod now runs on
    /// PlayerAnimationLib, and the emotes came out broken. Only the built-in poses show.
    /// </summary>
    private const bool EmotesEnabled = false;

    public void WatchHomeEmotes()
    {
        if (_watchingEmotes)
        {
            return;
        }

        _watchingEmotes = true;
        PropertyChanged += OnEmoteSourceChanged;
        RefreshHomePoses();

        if (EmotesEnabled)
        {
            _ = RefreshHomeEmotesAsync();
        }
    }

    private void OnEmoteSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SelectedInstance):
                if (EmotesEnabled) { _ = RefreshHomeEmotesAsync(); }
                break;

            // The mod names its own emotes in the game's languages; ours follow the launcher's.
            case nameof(Language):
                RefreshHomePoses();
                if (EmotesEnabled) { _ = RefreshHomeEmotesAsync(); }
                break;

            // Coming back from the builds page, where the mod may just have been added or
            // removed. An unchanged build costs a directory listing here, not a re-read.
            case nameof(Section) when Section == ShellSection.Game:
                if (EmotesEnabled) { _ = RefreshHomeEmotesAsync(); }
                break;
        }
    }

    private void RefreshHomePoses()
    {
        HomePoses.Clear();

        foreach (var (key, fallback) in SkinViewer.BuiltInPoses)
        {
            HomePoses.Add(new PoseChoice(key, Localize(key, fallback)));
        }
    }

    private async Task RefreshHomeEmotesAsync()
    {
        var load = ++_emoteLoad;
        IReadOnlyList<Emote> emotes = Array.Empty<Emote>();

        if (SelectedInstance is { } instance)
        {
            try
            {
                var directory = _instances.GameDirectory(instance);
                var locale = Language == "en" ? "en_us" : "ru_ru";

                // Jars are opened and files parsed: not on the thread that draws.
                emotes = await Task.Run(() => _emoteLibrary.Load(directory, locale));
            }
            catch (Exception)
            {
                // The figure has its own poses; a build whose emotes cannot be read just adds none.
            }
        }

        // The player may have picked another build while this one was being read.
        if (load == _emoteLoad)
        {
            HomeEmotes = emotes;
        }
    }
}

/// <summary>One of the figure's own poses in the list over the stage.</summary>
public sealed record PoseChoice(string Key, string Name);
