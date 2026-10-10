using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using STlauncher.App.Services;
using STlauncher.App.ViewModels;

namespace STlauncher.App.Views;

/// <summary>
/// The question asked before the usual interface when the previous start never reached a
/// usable window: reinstall the launcher's files, or not now. Built in code and fed with
/// delegates, so it depends on nothing that a broken start may have failed to build:
/// no view model, no services, no XAML of its own.
/// </summary>
/// <remarks>
/// Nothing is downloaded or replaced until the player presses the button, and closing the
/// window by its cross is "not now".
/// </remarks>
public sealed class StartupRepairDialog : Window
{
    private readonly string _version;
    private readonly Func<IProgress<int>, CancellationToken, Task<RepairResult>> _prepare;
    private readonly Func<bool> _apply;
    private readonly Action _proceed;
    private readonly string _installerUrl;

    private readonly TextBlock _message;
    private readonly TextBlock _details;
    private readonly ProgressBar _progress;
    private readonly Button _primary;
    private readonly Button _secondary;

    private CancellationTokenSource? _work;
    private bool _proceeded;
    private bool _failed;

    /// <param name="version">The installed version, for the text.</param>
    /// <param name="again">This version's files were already reinstalled once and the start failed anyway.</param>
    /// <param name="prepare">Fetches and checks the package. Called only from the button.</param>
    /// <param name="apply">Replaces the files and restarts; does not return on success.</param>
    /// <param name="proceed">Starts the usual interface. Called exactly once, unless the repair restarts the launcher.</param>
    public StartupRepairDialog(
        string version,
        bool again,
        Func<IProgress<int>, CancellationToken, Task<RepairResult>> prepare,
        Func<bool> apply,
        Action proceed,
        string installerUrl)
    {
        _version = version;
        _prepare = prepare;
        _apply = apply;
        _proceed = proceed;
        _installerUrl = installerUrl;

        Title = "STlauncher";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("AppBgBrush");

        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://STlauncher.App/Assets/app.ico")));
        }
        catch (Exception)
        {
            // A window without an icon still asks its question.
        }

        _message = new TextBlock
        {
            Text = again
                ? Text("SelfRepair_QuestionAgain", "The launcher could not open again. This version's files have already been reinstalled once. Reinstall them one more time?")
                : Text("SelfRepair_Question", "Last time the launcher could not open. Check and reinstall its files?"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            Foreground = Brush("TextPrimaryBrush")
        };

        _details = new TextBlock
        {
            Text = Text("SelfRepair_Details", "The launcher will download version {0} again, replace its program files and restart. Builds, worlds and settings are not touched.", version),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = Brush("TextSecondaryBrush")
        };

        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, IsVisible = false };

        _primary = new Button
        {
            Content = again ? Text("SelfRepair_CheckAgain", "Reinstall") : Text("SelfRepair_Check", "Check"),
            IsDefault = true,
            MinWidth = 120,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _primary.Classes.Add("primary");
        _primary.Click += (_, _) => OnPrimary();

        _secondary = new Button
        {
            Content = Text("SelfRepair_NotNow", "Not now"),
            IsCancel = true,
            MinWidth = 120,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _secondary.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24, 22, 24, 20),
            Spacing = 12,
            Children =
            {
                _message,
                _details,
                _progress,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { _secondary, _primary }
                }
            }
        };

        // Every way out that is not a restart leads to the usual start, and the main
        // window has to be up before this one goes, or the application would end with it.
        Closing += (_, _) => Proceed();
    }

    private void Proceed()
    {
        if (_proceeded)
        {
            return;
        }

        _proceeded = true;
        _work?.Cancel();

        try
        {
            _proceed();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "start after the repair question failed");
            throw;
        }
    }

    private void OnPrimary()
    {
        if (_failed)
        {
            OpenInstaller();
            return;
        }

        _ = RepairAsync();
    }

    private async Task RepairAsync()
    {
        _primary.IsEnabled = false;
        _progress.IsVisible = true;
        _progress.IsIndeterminate = true;
        _details.Text = Text("SelfRepair_Working", "Downloading the launcher's files: {0}%", 0);

        _work = new CancellationTokenSource();
        var token = _work.Token;

        // Progress arrives from the download's thread.
        var progress = new Progress<int>(percent => Dispatcher.UIThread.Post(() =>
        {
            if (_failed || _proceeded)
            {
                return;
            }

            _progress.IsIndeterminate = false;
            _progress.Value = percent;
            _details.Text = Text("SelfRepair_Working", "Downloading the launcher's files: {0}%", percent);
        }));

        RepairResult result;
        string? reason = null;

        try
        {
            result = await Task.Run(() => _prepare(progress, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            result = RepairResult.Failed;
            reason = ex.Message;
        }

        if (_proceeded)
        {
            return;
        }

        if (result == RepairResult.Ready)
        {
            _secondary.IsEnabled = false;
            _progress.IsIndeterminate = true;
            _details.Text = Text("SelfRepair_Applying", "Replacing the files and restarting the launcher…");

            try
            {
                // On success the process ends inside this call.
                if (_apply())
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                CrashLog.Write(ex, "reinstalling the launcher's files failed");
            }

            result = RepairResult.Failed;
        }

        ShowFailure(result, reason);
    }

    /// <summary>
    /// Said as it is: the files were not replaced, and what is left is the installer,
    /// which the player downloads and runs by hand.
    /// </summary>
    private void ShowFailure(RepairResult result, string? reason)
    {
        _failed = true;
        _progress.IsVisible = false;

        _message.Text = Text("SelfRepair_FailedTitle", "The files could not be reinstalled from here.");
        _details.Text = result == RepairResult.VersionNotOffered
            ? Text("SelfRepair_NotOffered", "The update sources no longer carry version {0}, so it cannot be put back in place. You can download the installer of the current version and run it: builds, worlds and settings stay.", _version)
            : string.IsNullOrWhiteSpace(reason)
                ? Text("SelfRepair_Failed", "The launcher's files could not be downloaded. You can download the installer in the browser and run it: builds, worlds and settings stay.")
                : Text("SelfRepair_FailedWhy", "The launcher's files could not be downloaded ({0}). You can download the installer in the browser and run it: builds, worlds and settings stay.", reason);

        _primary.Content = Text("SelfRepair_OpenInstaller", "Download the installer");
        _primary.IsEnabled = true;
        _secondary.Content = Text("SelfRepair_Continue", "Continue starting");
        _secondary.IsEnabled = true;
    }

    private void OpenInstaller()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_installerUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser to hand the link to; it stays on screen for the player to copy.
        }

        _details.Text = Text("SelfRepair_InstallerOpened", "The installer is downloading in the browser: {0}. Close the launcher before running it.", _installerUrl);
    }

    private static string Text(string key, string fallback, params object?[] args)
        => MainWindowViewModel.Localize(key, fallback, args);

    private static IBrush? Brush(string key)
        => Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var value)
            ? value as IBrush
            : null;
}
