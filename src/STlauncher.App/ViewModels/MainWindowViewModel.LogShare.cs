using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Diagnostics;

namespace STlauncher.App.ViewModels;

/// <summary>
/// "Share the log": the crash report as a link instead of a wall of text in a chat. The
/// text goes to mclo.gs, a public paste for Minecraft logs, so two things hold: nothing
/// is sent before the player presses the button under the note that says so, and what is
/// sent has been through <see cref="LogRedactor"/> first.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>True while the note with "Publish" under it is up.</summary>
    [ObservableProperty]
    private bool _isLogShareOpen;

    [ObservableProperty]
    private bool _isLogSharing;

    /// <summary>The link of the last published log, kept on the card in case the clipboard was lost.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogShareLink))]
    private string _logShareLink = string.Empty;

    public bool HasLogShareLink => LogShareLink.Length > 0;

    /// <summary>Only opens the note. No request is made here.</summary>
    [RelayCommand]
    private void OpenLogShare() => IsLogShareOpen = true;

    [RelayCommand]
    private void CancelLogShare() => IsLogShareOpen = false;

    /// <summary>Redacts the report, publishes it and puts the link on the clipboard. Only from the button under the note.</summary>
    [RelayCommand]
    private async Task ShareCrashLogAsync()
    {
        if (IsLogSharing)
        {
            return;
        }

        try
        {
            IsLogSharing = true;
            Status = Localize("LogShare_Sending", "Publishing the log…");

            var text = LogRedactor.Redact(
                BuildCrashReportText(),
                LogRedactionContext.ForThisMachine(PublicServerAddresses()));

            var outcome = await _reportSender.ShareLogAsync(text);

            if (outcome.Sent)
            {
                LogShareLink = outcome.Message;
                IsLogShareOpen = false;
                await CopyToClipboardAsync(outcome.Message);
                AppendConsole($"[report] log published: {outcome.Message}");
                Status = Localize("LogShare_Done", "The link is in the clipboard: {0}", outcome.Message);
            }
            else
            {
                AppendConsole($"[report] log not published: {outcome.Message}");
                Status = Localize("LogShare_Failed", "Could not publish the log ({0}). Use “Copy report” instead.", outcome.Message);
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[report] log share failed: {ex.Message}");
            Status = Localize("LogShare_Failed", "Could not publish the log ({0}). Use “Copy report” instead.", ex.Message);
        }
        finally
        {
            IsLogSharing = false;
        }
    }

    [RelayCommand]
    private async Task CopyLogShareLinkAsync()
    {
        if (HasLogShareLink)
        {
            await CopyToClipboardAsync(LogShareLink);
            Status = Localize("LogShare_Done", "The link is in the clipboard: {0}", LogShareLink);
        }
    }
}
