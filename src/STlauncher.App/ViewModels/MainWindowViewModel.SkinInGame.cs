using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Instances;
using STlauncher.Core.Mods;
using STlauncher.Core.Skins;

namespace STlauncher.App.ViewModels;

/// <summary>
/// "Show my skin in the game": the switch in a build's settings, the line on the skins
/// page, and the hand-over of the worn skin before a launch.
/// </summary>
/// <remarks>
/// The rules are in <see cref="InGameSkin"/>. What this side adds is the consent: the mod
/// is downloaded only from the switch, and the text under it says what that click does
/// and who will see the skin.
/// </remarks>
public partial class MainWindowViewModel
{
    private int _skinInGameRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSkinInGameOn), nameof(SkinInGameSwitch), nameof(CanToggleSkinInGame), nameof(SkinInGameNote), nameof(HasSkinInGameNote))]
    [NotifyPropertyChangedFor(nameof(IsSkinInGameLineVisible), nameof(IsSkinInGameLinkVisible), nameof(SkinInGameLine))]
    private InGameSkinStatus _skinInGameStatus = new(InGameSkinState.Off);

    /// <summary>The mod is being downloaded, or a jar renamed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleSkinInGame), nameof(SkinInGameNote), nameof(HasSkinInGameNote))]
    private bool _isSkinInGameBusy;

    /// <summary>Why the last flip of the switch did not happen. Cleared by the next one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkinInGameProblem))]
    private string _skinInGameProblem = string.Empty;

    public bool HasSkinInGameProblem => SkinInGameProblem.Length > 0;

    public bool IsSkinInGameOn => SkinInGameStatus.IsActive;

    /// <summary>
    /// What the switch is bound to, both ways. The switch moves under the finger before
    /// anything has happened; written here, the move is a request, and when the request
    /// comes to nothing - no download, another skin mod - the switch is told to read the
    /// real state again and goes back by itself.
    /// </summary>
    public bool SkinInGameSwitch
    {
        get => IsSkinInGameOn;
        set
        {
            if (value != IsSkinInGameOn)
            {
                _ = ToggleSkinInGameAsync();
            }
        }
    }

    /// <summary>
    /// Off for a build that cannot have the mod, and while the game runs: its jars are
    /// open then, and a rename would fail halfway.
    /// </summary>
    public bool CanToggleSkinInGame
        => SelectedInstance is not null && !IsSkinInGameBusy && !IsGameRunning &&
           SkinInGameStatus.State is not (InGameSkinState.NoLoader or InGameSkinState.OtherSkinMod);

    /// <summary>What is true of this build right now, under the paragraph that explains the switch.</summary>
    public string SkinInGameNote
    {
        get
        {
            if (IsSkinInGameBusy)
            {
                return Localize("SkinInGame_Working", "Working on it…");
            }

            var mod = SkinInGameStatus.Mod;

            return SkinInGameStatus.State switch
            {
                InGameSkinState.NoLoader => Localize("SkinInGame_NoLoader", "This build has no mod loader, so there is nowhere to put the mod. Choose Fabric, Quilt, Forge or NeoForge above."),
                InGameSkinState.OtherSkinMod => Localize("SkinInGame_OtherMod", "This build already has a skin mod: {0} ({1}). The launcher does not add a second one - two of them get in each other's way.", mod?.Name, mod?.FileName),
                InGameSkinState.Off when mod is not null => Localize("SkinInGame_OwnModOff", "This build already has CustomSkinLoader ({0}). Nothing will be downloaded: the launcher will only hand it your skin.", mod.FileName),
                InGameSkinState.On => WithWornSkin(Localize("SkinInGame_On", "CustomSkinLoader {0} is in the build.", SelectedInstance?.SkinInGame?.ModVersion)),
                InGameSkinState.OnWithOwnMod => WithWornSkin(Localize("SkinInGame_OnOwnMod", "The skin is handed to the CustomSkinLoader this build already had ({0}).", mod?.FileName)),
                InGameSkinState.ModRemoved => Localize("SkinInGame_Removed", "The mod was removed from the build or switched off, and the launcher does not bring it back by itself. Turn the switch on to install it again."),
                _ => string.Empty
            };
        }
    }

    public bool HasSkinInGameNote => SkinInGameNote.Length > 0;

    /// <summary>The second half of the "on" line: which skin goes into the game, or that none is worn.</summary>
    private string WithWornSkin(string text)
        => text + " " + (WornSkinForGame() is { } worn
            ? Localize("SkinInGame_WornNow", "At the next launch the game gets «{0}».", worn.Name)
            : Localize("SkinInGame_NothingWorn", "No library skin is worn right now, so the game will show whatever the mod finds itself. Put one on in the Skins section."));

    /// <summary>The library skin the figure has on. One that was taken off is in the library but not worn.</summary>
    private SkinEntry? WornSkinForGame() => IsSkinSourceLibrary ? _skins.Library.Worn : null;

    // ===================== The skins page =====================

    /// <summary>"In the game this skin is on for the build ...": only under the skin that is worn.</summary>
    public bool IsSkinInGameLineVisible => IsSkinInGameOn && SelectedLibrarySkin is { IsWorn: true };

    /// <summary>Otherwise the way to the switch, for whoever wonders why the game still shows Steve.</summary>
    public bool IsSkinInGameLinkVisible => !IsSkinInGameLineVisible && SelectedInstance is not null && HasSelectedLibrarySkin;

    public string SkinInGameLine => Localize("SkinInGame_PageOn", "In the game this skin is on for the build «{0}».", SelectedInstance?.Name);

    private void RaiseSkinInGamePage()
    {
        OnPropertyChanged(nameof(IsSkinInGameLineVisible));
        OnPropertyChanged(nameof(IsSkinInGameLinkVisible));
        OnPropertyChanged(nameof(SkinInGameLine));
        OnPropertyChanged(nameof(SkinInGameNote));
    }

    // ===================== Keeping the state current =====================

    /// <summary>Called once from the constructor.</summary>
    private void AttachSkinInGame() => PropertyChanged += OnSkinInGameRelevantChange;

    private void OnSkinInGameRelevantChange(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SelectedInstance):
                SkinInGameProblem = string.Empty;
                _ = RefreshSkinInGameAsync();
                break;

            // The loader decides whether the mod can be there at all; the mod list's
            // summary changes when a jar is added, removed or switched.
            case nameof(SelectedLoader):
            case nameof(InstalledModsSummary):
            case nameof(IsBuildSettingsOpen):
            case nameof(Section):
                _ = RefreshSkinInGameAsync();
                break;

            case nameof(IsGameRunning):
                OnPropertyChanged(nameof(CanToggleSkinInGame));
                break;

            // Raised by everything that changes which skin is worn - some of it a step
            // before the cards are told, hence the post.
            case nameof(SelectedLibrarySkin):
            case nameof(SkinSourceLabel):
            case nameof(IsSkinSourceLibrary):
                Avalonia.Threading.Dispatcher.UIThread.Post(RaiseSkinInGamePage);
                break;
        }
    }

    /// <summary>
    /// Looks at the build's mods folder off the UI thread: it opens every jar to ask
    /// what it is. Only while somebody can see the answer - the settings sheet or the
    /// skins page.
    /// </summary>
    private async Task RefreshSkinInGameAsync()
    {
        var run = ++_skinInGameRun;

        if (SelectedInstance is not { } instance)
        {
            SkinInGameStatus = new InGameSkinStatus(InGameSkinState.Off);
            return;
        }

        if (!IsBuildSettingsOpen && Section != ShellSection.Skins)
        {
            return;
        }

        InGameSkinStatus status;

        try
        {
            var directory = _instances.GameDirectory(instance);
            status = await Task.Run(() => InGameSkin.Inspect(instance, directory));
        }
        catch (Exception ex)
        {
            AppendConsole($"[skin] {ex.Message}");
            return;
        }

        if (run == _skinInGameRun)
        {
            SkinInGameStatus = status;
        }
    }

    // ===================== The switch =====================

    [RelayCommand]
    private async Task ToggleSkinInGameAsync()
    {
        if (SelectedInstance is not { } instance || IsSkinInGameBusy || IsGameRunning)
        {
            return;
        }

        var directory = _instances.GameDirectory(instance);
        IsSkinInGameBusy = true;
        SkinInGameProblem = string.Empty;

        try
        {
            var status = await Task.Run(() => InGameSkin.Inspect(instance, directory));

            if (status.IsActive)
            {
                if (InGameSkin.Disable(instance, directory) is { } jar)
                {
                    RenameSkinModRecord(instance, jar, jar + ".disabled", disabled: true);
                    AppendConsole($"[skin] {jar} switched off in {instance.Name}; the config stays.");
                }

                _instances.Save(instance);
                return;
            }

            var result = await _skins.InGame.EnableAsync(instance, directory);

            switch (result)
            {
                case InGameSkinEnableResult.Installed when instance.SkinInGame is { Jar: { } jar } settings:
                    RecordInstalledMod(instance, new InstalledModRecord
                    {
                        FileName = jar,
                        Source = ModSource.Modrinth,
                        Id = InGameSkinMod.ProjectSlug,
                        Name = InGameSkinMod.DisplayName,
                        Folder = ModManager.ModsFolderName,
                        Version = settings.ModVersion
                    });
                    AppendConsole($"[skin] {jar} installed into {instance.Name} from Modrinth, SHA-512 checked.");
                    break;

                case InGameSkinEnableResult.SwitchedBackOn when instance.SkinInGame is { Jar: { } jar }:
                    RenameSkinModRecord(instance, jar + ".disabled", jar, disabled: false);
                    break;

                case InGameSkinEnableResult.NoVersion:
                    SkinInGameProblem = Localize(
                        "SkinInGame_NoVersion",
                        "CustomSkinLoader is not published for {0} on {1}, so there is nothing to install for this build.",
                        instance.VersionId,
                        instance.Loader);
                    break;
            }

            _instances.Save(instance);

            // The skin goes in at once, so the very next launch already has it.
            PrepareSkinInGame(instance);
        }
        catch (Exception ex)
        {
            SkinInGameProblem = Localize("SkinInGame_Failed", "The mod could not be installed: {0}", ex.Message);
            AppendConsole($"[skin] {ex}");
        }
        finally
        {
            IsSkinInGameBusy = false;

            if (ReferenceEquals(SelectedInstance, instance))
            {
                // The mod list shows the new jar, or the one that was switched off.
                RefreshMods();
            }

            await RefreshSkinInGameAsync();
            OnPropertyChanged(nameof(SkinInGameSwitch));
        }
    }

    /// <summary>The mod list keeps a record per file name; a renamed jar keeps its record.</summary>
    private static void RenameSkinModRecord(Instance instance, string from, string to, bool disabled)
    {
        var record = instance.InstalledMods.FirstOrDefault(m =>
            string.Equals(m.FileName, from, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.Folder ?? ModManager.ModsFolderName, ModManager.ModsFolderName, StringComparison.OrdinalIgnoreCase));

        if (record is not null)
        {
            record.FileName = to;
            record.DisabledByUser = disabled;
        }
    }

    // ===================== Before a launch =====================

    /// <summary>
    /// Hands the worn skin to the mod in a build that has the switch on. Never downloads,
    /// and never stops a launch: a skin that could not be copied is a line in the console.
    /// </summary>
    private void PrepareSkinInGame(Instance? instance)
    {
        if (instance?.SkinInGame is not { Enabled: true })
        {
            return;
        }

        try
        {
            var worn = WornSkinForGame();

            var result = InGameSkin.Refresh(
                instance,
                _instances.GameDirectory(instance),
                Username,
                worn is null ? null : _skins.Library.PathOf(worn.Id),
                worn?.SkinModel == SkinModel.Slim);

            if (result.RecordChanged)
            {
                _instances.Save(instance);
            }

            AppendConsole(result.State switch
            {
                InGameSkinState.On or InGameSkinState.OnWithOwnMod when result.SkinFile is not null
                    => $"[skin] «{worn?.Name}» handed to CustomSkinLoader as {result.SkinFile} ({InGameSkinConfig.ModelName(worn?.SkinModel == SkinModel.Slim)}).",
                InGameSkinState.On or InGameSkinState.OnWithOwnMod
                    => "[skin] no library skin is worn; CustomSkinLoader will use its other sources.",
                InGameSkinState.ModRemoved
                    => "[skin] CustomSkinLoader was removed from the build or switched off; the launcher does not bring it back until the switch is flipped again.",
                _ => $"[skin] not handed over: {result.State}."
            });
        }
        catch (Exception ex)
        {
            AppendConsole($"[skin] the skin could not be prepared: {ex.Message}");
        }
    }
}
