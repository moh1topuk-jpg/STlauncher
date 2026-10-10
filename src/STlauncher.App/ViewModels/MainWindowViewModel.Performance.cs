using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Boost;
using STlauncher.Core.Launch;

namespace STlauncher.App.ViewModels;

/// <summary>
/// One button instead of twelve settings: a preset writes the graphics options the game
/// reads at start and sets the memory for this machine. What each of them was before is
/// written into the build's record (<see cref="PresetRecord"/>), so the preset can be
/// taken back: every setting that still holds what the preset wrote returns to its old
/// value, and one the player has changed since stays the player's.
/// </summary>
/// <remarks>
/// A preset adds no mods. It used to, with no record of which and no way back; mods for
/// speed are added in one place only, by the "Ускорение" switch right below the presets,
/// which lists them first and switches them off again.
/// </remarks>
public partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevertPreset))]
    private bool _isApplyingPreset;

    [ObservableProperty]
    private string _presetStatus = string.Empty;

    /// <summary>A preset was applied to this build and not taken back: the way back is offered.</summary>
    public bool HasPresetRecord => SelectedInstance?.Preset is { IsEmpty: false };

    public bool CanRevertPreset => HasPresetRecord && !IsApplyingPreset;

    /// <summary>The preset applied last and not taken back, for the picker to mark. Nothing is decided by it.</summary>
    private string? MarkedPreset => HasPresetRecord ? SelectedInstance?.Preset?.Preset : null;

    public bool IsPresetLow => string.Equals(MarkedPreset, nameof(PerformancePreset.Low), StringComparison.OrdinalIgnoreCase);

    public bool IsPresetBalanced => string.Equals(MarkedPreset, nameof(PerformancePreset.Balanced), StringComparison.OrdinalIgnoreCase);

    public bool IsPresetHigh => string.Equals(MarkedPreset, nameof(PerformancePreset.High), StringComparison.OrdinalIgnoreCase);

    /// <summary>Another build is open, or the record changed: the button reads its state again.</summary>
    private void RaisePresetState(bool buildChanged = false)
    {
        if (buildChanged)
        {
            // The line was about the build that was open before.
            PresetStatus = string.Empty;
        }

        OnPropertyChanged(nameof(HasPresetRecord));
        OnPropertyChanged(nameof(CanRevertPreset));
        OnPropertyChanged(nameof(IsPresetLow));
        OnPropertyChanged(nameof(IsPresetBalanced));
        OnPropertyChanged(nameof(IsPresetHigh));
    }

    [RelayCommand]
    private async Task ApplyPerformancePresetAsync(string? preset)
    {
        if (IsApplyingPreset || !Enum.TryParse<PerformancePreset>(preset, ignoreCase: true, out var parsed))
        {
            return;
        }

        if (IsGameRunning)
        {
            PresetStatus = Localize("Perf_GameRunning", "The game is running: it would write its own settings over these on exit. Close it first.");
            return;
        }

        var instance = SelectedInstance;
        var directory = InstanceDirectory;
        var earlier = instance?.Preset;

        try
        {
            IsApplyingPreset = true;

            var written = await Task.Run(() => GameOptions.ApplyPerformancePreset(directory, parsed, earlier?.Options));

            if (written is null)
            {
                PresetStatus = Localize("Perf_OptionsUnreadable", "options.txt cannot be read as text, so the preset changed nothing.");
                return;
            }

            // Memory follows the machine, not the preset: a weak PC gets less because it has
            // less, a strong one gets the recommended quarter of its RAM.
            var memoryBefore = (int)MaxMemoryMb;

            // The slider is the open build's: if another one was opened meanwhile, the
            // memory of the build the preset went to is left alone.
            if (ReferenceEquals(SelectedInstance, instance))
            {
                MaxMemoryMb = parsed == PerformancePreset.Low
                    ? Math.Min(RecommendedMemoryMb, 3072)
                    : RecommendedMemoryMb;
            }

            if (instance is not null)
            {
                var record = new PresetRecord
                {
                    Preset = parsed.ToString(),
                    AppliedAt = DateTimeOffset.Now,
                    Options = written
                };

                // As with the settings: a second preset over the first keeps the memory
                // from before the first as the value to go back to.
                var memoryNow = (int)MaxMemoryMb;
                var memoryPrevious = earlier is { MemoryWritten: { } was, MemoryPrevious: { } before } && was == memoryBefore
                    ? before
                    : memoryBefore;

                if (memoryPrevious != memoryNow)
                {
                    record.MemoryPrevious = memoryPrevious;
                    record.MemoryWritten = memoryNow;
                }

                instance.Preset = record.IsEmpty ? null : record;
                _instances.Save(instance);
                AppendConsole($"[perf] {parsed} for {instance.Name}: {written.Count} option(s) recorded, memory {memoryBefore} -> {memoryNow} MB");
            }

            PresetStatus = Localize("Perf_Applied", "Applied: graphics settings and {0} MB of memory. The game reads them on the next start.", (int)MaxMemoryMb);
        }
        catch (Exception ex)
        {
            PresetStatus = Localize("Perf_Failed", "Could not apply the preset: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsApplyingPreset = false;
            RaisePresetState();
        }
    }

    /// <summary>
    /// "Put the settings back as they were before the preset". Only what the preset wrote
    /// and nobody has touched since goes back; the rest is named and left.
    /// </summary>
    [RelayCommand]
    private async Task RevertPerformancePresetAsync()
    {
        if (IsApplyingPreset || SelectedInstance is not { Preset: { } record } instance)
        {
            return;
        }

        if (IsGameRunning)
        {
            PresetStatus = Localize("Perf_GameRunning", "The game is running: it would write its own settings over these on exit. Close it first.");
            return;
        }

        var directory = InstanceDirectory;
        var options = record.Options.ToList();

        try
        {
            IsApplyingPreset = true;

            var reverted = options.Count > 0
                ? await Task.Run(() => GameOptions.RevertPerformancePreset(directory, options))
                : new BoostOptionsRevert(Array.Empty<string>(), Array.Empty<string>());

            if (reverted is null)
            {
                // The record stays: the file may be readable next time.
                PresetStatus = Localize("Boost_OffOptionsUnreadable", "options.txt cannot be read as text, so the settings stay as they are.");
                return;
            }

            var parts = new List<string>();

            if (reverted.Restored.Count > 0)
            {
                parts.Add(Localize("Boost_OffOptions", "Settings put back: {0}.", reverted.Restored.Count));
            }

            if (record.MemoryWritten is { } memoryWritten && record.MemoryPrevious is { } memoryPrevious)
            {
                if ((int)MaxMemoryMb == memoryWritten && ReferenceEquals(SelectedInstance, instance))
                {
                    MaxMemoryMb = memoryPrevious;
                    parts.Add(Localize("Perf_RevertedMemory", "Memory: {0} MB again.", memoryPrevious));
                }
                else
                {
                    parts.Add(Localize("Perf_RevertLeftMemory", "Memory is left as you set it."));
                }
            }

            if (reverted.LeftToPlayer.Count > 0)
            {
                parts.Add(Localize("Boost_OffLeftOptions", "Left as you changed them: {0}.",
                    string.Join(", ", reverted.LeftToPlayer.Select(BoostOptionLabel))));
            }

            instance.Preset = null;
            _instances.Save(instance);
            AppendConsole($"[perf] preset taken back for {instance.Name}: {reverted.Restored.Count} option(s) restored, {reverted.LeftToPlayer.Count} left");

            PresetStatus = string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            PresetStatus = Localize("Perf_Failed", "Could not apply the preset: {0}", ex.Message);
            AppendConsole(ex.ToString());
        }
        finally
        {
            IsApplyingPreset = false;
            RaisePresetState();
        }
    }
}
