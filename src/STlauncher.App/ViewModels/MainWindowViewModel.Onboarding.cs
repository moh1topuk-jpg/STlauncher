using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Import;

namespace STlauncher.App.ViewModels;

/// <summary>One screen of the tour. Templates pick the view by type and bind through Owner.</summary>
public abstract record OnboardingPage(MainWindowViewModel Owner);

public sealed record OnboardingWelcomePage(MainWindowViewModel Owner) : OnboardingPage(Owner);

public sealed record OnboardingNicknamePage(MainWindowViewModel Owner) : OnboardingPage(Owner);

public sealed record OnboardingMemoryPage(MainWindowViewModel Owner) : OnboardingPage(Owner);

public sealed record OnboardingImportPage(MainWindowViewModel Owner) : OnboardingPage(Owner);

public sealed record OnboardingDonePage(MainWindowViewModel Owner) : OnboardingPage(Owner);

/// <summary>The screens of the first run, in order.</summary>
public enum OnboardingStep
{
    Welcome,
    Nickname,
    Memory,
    Import,
    Done
}

/// <summary>
/// The first run. Four short screens - who you are, how much memory, what to bring over
/// - and the launcher is set up the way it would take a new player ten minutes to find.
/// Every screen can be skipped; the whole thing can be skipped from the first one.
/// </summary>
public partial class MainWindowViewModel
{
    private static readonly OnboardingStep[] Steps =
    {
        OnboardingStep.Welcome,
        OnboardingStep.Nickname,
        OnboardingStep.Memory,
        OnboardingStep.Import,
        OnboardingStep.Done
    };

    [ObservableProperty]
    private bool _isOnboardingOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnboardingWelcome))]
    [NotifyPropertyChangedFor(nameof(IsOnboardingNickname))]
    [NotifyPropertyChangedFor(nameof(IsOnboardingMemory))]
    [NotifyPropertyChangedFor(nameof(IsOnboardingImport))]
    [NotifyPropertyChangedFor(nameof(IsOnboardingDone))]
    [NotifyPropertyChangedFor(nameof(OnboardingStepIndex))]
    [NotifyPropertyChangedFor(nameof(OnboardingNextLabel))]
    [NotifyPropertyChangedFor(nameof(OnboardingShowsProgress))]
    private OnboardingStep _onboardingStep = OnboardingStep.Welcome;

    /// <summary>The screen to show, as an object the content control can transition between.</summary>
    [ObservableProperty]
    private OnboardingPage? _onboardingPage;

    /// <summary>True while stepping back, so the slide runs the other way.</summary>
    [ObservableProperty]
    private bool _onboardingGoingBack;

    partial void OnOnboardingStepChanged(OnboardingStep value)
    {
        OnboardingPage = value switch
        {
            OnboardingStep.Welcome => new OnboardingWelcomePage(this),
            OnboardingStep.Nickname => new OnboardingNicknamePage(this),
            OnboardingStep.Memory => new OnboardingMemoryPage(this),
            OnboardingStep.Import => new OnboardingImportPage(this),
            _ => new OnboardingDonePage(this)
        };
    }

    public bool IsOnboardingWelcome => OnboardingStep == OnboardingStep.Welcome;
    public bool IsOnboardingNickname => OnboardingStep == OnboardingStep.Nickname;
    public bool IsOnboardingMemory => OnboardingStep == OnboardingStep.Memory;
    public bool IsOnboardingImport => OnboardingStep == OnboardingStep.Import;
    public bool IsOnboardingDone => OnboardingStep == OnboardingStep.Done;

    /// <summary>Zero-based position among the three middle screens, for the dots.</summary>
    public int OnboardingStepIndex => Math.Clamp(Array.IndexOf(Steps, OnboardingStep) - 1, 0, 2);

    public bool OnboardingShowsProgress => OnboardingStep is OnboardingStep.Nickname or OnboardingStep.Memory or OnboardingStep.Import;

    public string OnboardingNextLabel => OnboardingStep switch
    {
        OnboardingStep.Welcome => Localize("Onboarding_Start", "Let's go"),
        OnboardingStep.Import => Localize("Onboarding_Next", "Next"),
        OnboardingStep.Done => Localize("Onboarding_Finish", "Play"),
        _ => Localize("Onboarding_Next", "Next")
    };

    /// <summary>What the scan found, for the import screen.</summary>
    [ObservableProperty]
    private string _onboardingImportText = string.Empty;

    [ObservableProperty]
    private bool _onboardingImportScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnboardingHasImports))]
    private int _onboardingImportCount;

    public bool OnboardingHasImports => OnboardingImportCount > 0;

    /// <summary>Copies of the ticked builds are made; the alternative shares the folders.</summary>
    [ObservableProperty]
    private bool _onboardingImportByCopying;

    private bool _onboardingImported;

    /// <summary>
    /// Only a brand-new installation is walked through. Anyone with a settings file has
    /// been here before and has better things to do.
    /// </summary>
    private void StartOnboardingIfFirstRun(bool settingsExisted)
    {
        if (settingsExisted)
        {
            return;
        }

        OnboardingGoingBack = false;
        OnboardingStep = OnboardingStep.Welcome;
        OnboardingPage ??= new OnboardingWelcomePage(this);
        IsOnboardingOpen = true;
    }

    [RelayCommand]
    private void OnboardingNext()
    {
        var index = Array.IndexOf(Steps, OnboardingStep);

        if (OnboardingStep == OnboardingStep.Nickname)
        {
            Username = Username.Trim();

            if (!Core.Auth.OfflineAuth.IsValidUsername(Username))
            {
                Status = Localize("Status_InvalidNickname", "Nickname: 3-16 characters, letters, digits and underscore");
                return;
            }

            IsEditingNickname = false;
        }

        if (index + 1 >= Steps.Length)
        {
            FinishOnboarding();
            return;
        }

        OnboardingGoingBack = false;
        OnboardingStep = Steps[index + 1];

        if (OnboardingStep == OnboardingStep.Import)
        {
            _ = ScanForOnboardingImportsAsync();
        }
    }

    [RelayCommand]
    private void OnboardingBack()
    {
        var index = Array.IndexOf(Steps, OnboardingStep);

        if (index > 0)
        {
            OnboardingGoingBack = true;
            OnboardingStep = Steps[index - 1];
        }
    }

    /// <summary>Out of the tour, keeping whatever was already entered.</summary>
    [RelayCommand]
    private void SkipOnboarding() => FinishOnboarding();

    private void FinishOnboarding()
    {
        IsOnboardingOpen = false;
        IsEditingNickname = false;
        PersistSettings();
    }

    private async Task ScanForOnboardingImportsAsync()
    {
        if (OnboardingImportScanning || _onboardingImported)
        {
            return;
        }

        try
        {
            OnboardingImportScanning = true;
            OnboardingImportText = Localize("Onboarding_ImportScanning", "Looking through other launchers…");

            var found = await Task.Run(() => ExternalInstanceScanner.ScanAll());
            var usable = found.Where(i => i.IsUsable && !IsAlreadyLinked(i)).ToList();

            _importableBuilds = found;
            OnboardingImportCount = usable.Count;

            if (usable.Count == 0)
            {
                OnboardingImportText = Localize("Onboarding_ImportNone", "Nothing found - you are starting fresh. Builds can be imported later from the Builds page.");
                return;
            }

            var names = usable.Take(4).Select(i => i.Name).ToList();
            var more = usable.Count > names.Count ? Localize("Onboarding_ImportMore", " and {0} more", usable.Count - names.Count) : string.Empty;

            OnboardingImportText = Localize(
                "Onboarding_ImportFound",
                "Found {0} build(s): {1}{2}. Bring them over with worlds, mods and settings?",
                usable.Count,
                string.Join(", ", names),
                more);

            // Same list the import dialog shows, so "bring them over" needs no second scan.
            ShowCandidates(found);
        }
        catch (Exception ex)
        {
            OnboardingImportText = Localize("Import_Failed", "Import failed: {0}", ex.Message);
        }
        finally
        {
            OnboardingImportScanning = false;
        }
    }

    /// <summary>Imports everything the scan found, with the mode chosen on the screen.</summary>
    [RelayCommand]
    private async Task OnboardingImportAsync()
    {
        if (OnboardingImportCount == 0 || IsImportBusy)
        {
            return;
        }

        ImportByCopying = OnboardingImportByCopying;
        await ImportSelectedAsync();

        _onboardingImported = true;
        OnboardingImportCount = 0;
        OnboardingImportText = ImportStatus;
        ImportSuggestionText = string.Empty;
    }
}
