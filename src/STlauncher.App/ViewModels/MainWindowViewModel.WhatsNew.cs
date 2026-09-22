using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Text;

namespace STlauncher.App.ViewModels;

/// <summary>One line of the "what's new" card: a group title or a bullet under it.</summary>
public sealed record WhatsNewLine(string Text, bool IsHeading);

/// <summary>
/// What changed in the running version, shown once after an update. The text is the
/// launcher's own CHANGELOG.md, embedded at build time - the same section CI publishes
/// as the release notes, so nothing has to be fetched and nothing can disagree.
/// </summary>
public partial class MainWindowViewModel
{
    public ObservableCollection<WhatsNewLine> WhatsNewLines { get; } = new();

    [ObservableProperty]
    private string _whatsNewTitle = string.Empty;

    /// <summary>The card on the main screen: up after an update, gone once closed.</summary>
    [ObservableProperty]
    private bool _showWhatsNew;

    /// <summary>True when the running version has notes at all, for the settings button.</summary>
    [ObservableProperty]
    private bool _hasWhatsNew;

    private string? _lastSeenVersion;

    private void LoadWhatsNew(string? lastSeenVersion)
    {
        _lastSeenVersion = lastSeenVersion;
        WhatsNewLines.Clear();
        HasWhatsNew = false;
        ShowWhatsNew = false;

        var version = _updates.CurrentVersion;

        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://STlauncher.App/Assets/CHANGELOG.md"));
            using var reader = new StreamReader(stream);
            var notes = ChangelogReader.SectionFor(reader.ReadToEnd(), version!);

            if (notes is null || notes.IsEmpty)
            {
                return;
            }

            WhatsNewTitle = Localize("WhatsNew_Title", "What's new in {0}", version);

            foreach (var group in notes.Groups)
            {
                WhatsNewLines.Add(new WhatsNewLine(group.Title, IsHeading: true));

                foreach (var item in group.Items)
                {
                    WhatsNewLines.Add(new WhatsNewLine(ChangelogReader.Plain(item), IsHeading: false));
                }
            }

            HasWhatsNew = true;

            // First start of this version: the card is up until the player closes it.
            ShowWhatsNew = !string.Equals(lastSeenVersion, version, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppendConsole($"[whatsnew] {ex.Message}");
        }
    }

    [RelayCommand]
    private void DismissWhatsNew()
    {
        ShowWhatsNew = false;
        _lastSeenVersion = _updates.CurrentVersion;
        PersistSettings();
    }

    /// <summary>From the settings: bring the card back on the main screen.</summary>
    [RelayCommand]
    private void OpenWhatsNew()
    {
        if (HasWhatsNew)
        {
            ShowWhatsNew = true;
            Section = ShellSection.Game;
        }
    }
}
