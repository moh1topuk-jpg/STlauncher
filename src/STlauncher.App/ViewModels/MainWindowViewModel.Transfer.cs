using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using STlauncher.Core.Boost;
using STlauncher.Core.Instances;
using STlauncher.Core.Launch;

namespace STlauncher.App.ViewModels;

/// <summary>One line of the transfer's list: the setting, what it says here, what it will say.</summary>
public sealed record ControlsDiffLine(string Name, string Group, string From, string To);

/// <summary>
/// Controls from one build into another: key bindings, mouse, sound volumes, chat and
/// accessibility, field of view and interface scale - a fixed list (see
/// <see cref="ControlsTransfer"/>), never graphics or the pack order. The player sees
/// every value that will change before anything is written, and the build keeps a record
/// so that "put it back" undoes exactly the transfer. A new build takes the controls of
/// the build played last, unless the box is cleared.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Every build but the selected one, for the "take controls from" picker.</summary>
    public ObservableCollection<Instance> OtherInstances { get; } = new();

    [ObservableProperty]
    private Instance? _copySettingsSource;

    public bool HasOtherInstances => OtherInstances.Count > 0;

    /// <summary>The wizard's checkbox: take the controls of the build played last into the new one.</summary>
    [ObservableProperty]
    private bool _newBuildCopySettings = true;

    /// <summary>The build a new one takes its controls from: the one played last, else the open one.</summary>
    private Instance? NewBuildControlsSource
        => _allInstances.Where(i => i.LastPlayedAt is not null).OrderByDescending(i => i.LastPlayedAt).FirstOrDefault()
           ?? SelectedInstance;

    public string NewBuildCopySettingsLabel
        => Localize("Builds_WizardCopySettings", "Take the controls from “{0}”", NewBuildControlsSource?.Name ?? string.Empty);

    public bool CanCopyNewBuildSettings => NewBuildControlsSource is not null;

    private void RefreshOtherInstances()
    {
        var keep = CopySettingsSource?.Id;

        OtherInstances.Clear();

        foreach (var instance in _allInstances.Where(i => !ReferenceEquals(i, SelectedInstance)))
        {
            OtherInstances.Add(instance);
        }

        CopySettingsSource = OtherInstances.FirstOrDefault(i => i.Id == keep)
                             ?? OtherInstances.OrderByDescending(i => i.LastPlayedAt ?? DateTimeOffset.MinValue).FirstOrDefault();

        CloseControlsReview();
        OnPropertyChanged(nameof(HasOtherInstances));
        RefreshHomeBuilds();
        OnPropertyChanged(nameof(NewBuildCopySettingsLabel));
        OnPropertyChanged(nameof(CanCopyNewBuildSettings));
        RaiseControlsRecord();
    }

    partial void OnCopySettingsSourceChanged(Instance? value) => CloseControlsReview();

    // ===================== The review =====================

    public ObservableCollection<ControlsDiffLine> ControlsDiffLines { get; } = new();

    [ObservableProperty]
    private bool _isControlsReviewOpen;

    [ObservableProperty]
    private string _controlsReviewTitle = string.Empty;

    /// <summary>What is left out and why: mod keys of mods this build lacks, keys across 1.13.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasControlsReviewNote))]
    private string _controlsReviewNote = string.Empty;

    public bool HasControlsReviewNote => ControlsReviewNote.Length > 0;

    public bool HasControlsChanges => ControlsDiffLines.Count > 0;

    private ControlsPlan? _controlsPlan;
    private Instance? _controlsPlanSource;
    private Instance? _controlsPlanTarget;

    /// <summary>"Show what changes": reads both files and lists every value that differs. Writes nothing.</summary>
    [RelayCommand]
    private void ReviewControlsTransfer()
    {
        var source = CopySettingsSource;
        var target = SelectedInstance;

        CloseControlsReview();

        if (source is null || target is null || ControlsLocked(source, target))
        {
            return;
        }

        var sourceFile = OptionsFile.TryLoad(Path.Combine(_instances.GameDirectory(source), GameOptions.FileName));
        var targetFile = OptionsFile.TryLoad(Path.Combine(_instances.GameDirectory(target), GameOptions.FileName));

        if (sourceFile is null || targetFile is null)
        {
            Status = Localize("Transfer_Unreadable", "options.txt of one of the builds cannot be read; nothing was changed");
            return;
        }

        if (sourceFile.Get("version") is null && !sourceFile.Keys().Any(ControlsTransfer.IsSafeKey))
        {
            Status = Localize("Transfer_Nothing", "“{0}” has no saved controls yet: start the game there once", source.Name);
            return;
        }

        var plan = ControlsTransfer.Plan(sourceFile, source.VersionId, targetFile, target.VersionId);

        foreach (var change in plan.Changes)
        {
            ControlsDiffLines.Add(new ControlsDiffLine(
                ControlsName(change.Key),
                ControlsGroupName(change.Group),
                change.Current is null ? "—" : ControlsValue(change.Key, change.Current),
                ControlsValue(change.Key, change.Value)));
        }

        var notes = new List<string>();

        if (plan.KeysSkippedForVersion)
        {
            notes.Add(Localize("Transfer_KeysAcrossVersions", "Key bindings stay: before Minecraft 1.13 the game wrote them differently."));
        }

        if (plan.SkippedModKeys.Count > 0)
        {
            notes.Add(Localize("Transfer_ModKeysSkipped", "Keys of mods this build does not have are not carried: {0}.", plan.SkippedModKeys.Count));
        }

        _controlsPlan = plan;
        _controlsPlanSource = source;
        _controlsPlanTarget = target;

        ControlsReviewTitle = plan.Any
            ? Localize("Transfer_ReviewTitle", "From “{0}” {1} value(s) will change, {2} already match:", source.Name, plan.Changes.Count, plan.Unchanged)
            : Localize("Transfer_ReviewSame", "The controls of “{0}” and of this build already match.", source.Name);
        ControlsReviewNote = string.Join(" ", notes);
        IsControlsReviewOpen = true;
        OnPropertyChanged(nameof(HasControlsChanges));
    }

    [RelayCommand]
    private void CloseControlsReview()
    {
        _controlsPlan = null;
        _controlsPlanSource = null;
        _controlsPlanTarget = null;
        ControlsDiffLines.Clear();
        ControlsReviewNote = string.Empty;
        IsControlsReviewOpen = false;
        OnPropertyChanged(nameof(HasControlsChanges));
    }

    /// <summary>"Carry over": writes what the list showed, and keeps the record for the way back.</summary>
    [RelayCommand]
    private void ApplyControlsTransfer()
    {
        var plan = _controlsPlan;
        var source = _controlsPlanSource;
        var target = _controlsPlanTarget;

        if (plan is null || source is null || target is null || !plan.Any || !ReferenceEquals(target, SelectedInstance))
        {
            CloseControlsReview();
            return;
        }

        if (ControlsLocked(source, target))
        {
            return;
        }

        try
        {
            var applied = WriteControls(target, source, plan);
            CloseControlsReview();
            Status = applied
                ? Localize("Transfer_Done", "Controls from “{0}” carried over: {1} value(s). “Put it back” undoes it.", source.Name, plan.Changes.Count)
                : Localize("Transfer_Unreadable", "options.txt of one of the builds cannot be read; nothing was changed");
        }
        catch (Exception ex)
        {
            Status = Localize("Transfer_Failed", "Could not carry the controls over: {0}", ex.Message);
        }
    }

    /// <summary>Writes a plan into the target's options.txt and records it. False when the file cannot be read as text.</summary>
    private bool WriteControls(Instance target, Instance source, ControlsPlan plan)
    {
        var directory = _instances.GameDirectory(target);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, GameOptions.FileName);

        if (OptionsFile.TryLoad(path) is not { } file)
        {
            return false;
        }

        var earlier = target.ControlsTransfer?.Options;
        var records = ControlsTransfer.Apply(file, plan.Changes, earlier);

        file.Save(path);

        target.ControlsTransfer = new ControlsTransferRecord
        {
            FromName = source.Name,
            FromId = source.Id,
            AppliedAt = DateTimeOffset.Now,
            Options = records
        };

        _instances.Save(target);
        AppendConsole($"[transfer] {plan.Changes.Count} control value(s) from '{source.Name}' into '{target.Name}'");
        RaiseControlsRecord();
        return true;
    }

    // ===================== The way back =====================

    public bool HasControlsTransferRecord => SelectedInstance?.ControlsTransfer is { Options.Count: > 0 };

    public string ControlsTransferRecordLabel => SelectedInstance?.ControlsTransfer is { } record
        ? Localize("Transfer_RecordLabel", "Carried over from “{0}” on {1:d}: {2} value(s)",
            record.FromName ?? "?", record.AppliedAt?.LocalDateTime ?? DateTime.Now, record.Options.Count)
        : string.Empty;

    private void RaiseControlsRecord()
    {
        OnPropertyChanged(nameof(HasControlsTransferRecord));
        OnPropertyChanged(nameof(ControlsTransferRecordLabel));
    }

    /// <summary>"Put it back": every value the transfer wrote that still says so returns to what it was.</summary>
    [RelayCommand]
    private void RevertControlsTransfer()
    {
        var target = SelectedInstance;

        if (target?.ControlsTransfer is not { } record || ControlsLocked(null, target))
        {
            return;
        }

        try
        {
            var path = Path.Combine(_instances.GameDirectory(target), GameOptions.FileName);

            if (OptionsFile.TryLoad(path) is not { } file)
            {
                Status = Localize("Transfer_Unreadable", "options.txt of one of the builds cannot be read; nothing was changed");
                return;
            }

            var result = ControlsTransfer.Revert(file, record.Options);

            if (file.Changed)
            {
                file.Save(path);
            }

            target.ControlsTransfer = null;
            _instances.Save(target);
            RaiseControlsRecord();

            Status = result.LeftToPlayer.Count == 0
                ? Localize("Transfer_Reverted", "Controls are back as they were: {0} value(s)", result.Restored.Count)
                : Localize("Transfer_RevertedPartly", "Put back: {0}. Changed in the game since, left as you set them: {1}", result.Restored.Count, result.LeftToPlayer.Count);
            AppendConsole($"[transfer] reverted in '{target.Name}': {result.Restored.Count} restored, {result.LeftToPlayer.Count} left");
        }
        catch (Exception ex)
        {
            Status = Localize("Transfer_Failed", "Could not carry the controls over: {0}", ex.Message);
        }
    }

    /// <summary>Either game running means its options.txt is about to be written over on exit: refused.</summary>
    private bool ControlsLocked(Instance? source, Instance target)
    {
        var running = new[] { source, target }
            .Where(i => i is not null)
            .FirstOrDefault(i => RunningGames.IsRunning(_instances.GameDirectory(i!)));

        if (running is null && !(IsGameRunning && ReferenceEquals(target, SelectedInstance)))
        {
            return false;
        }

        Status = Localize("Transfer_GameRunning", "Close the game in “{0}” first: it writes its settings over on exit", (running ?? target).Name);
        return true;
    }

    /// <summary>Called by the wizard after the new build's folder exists.</summary>
    private void CopySettingsIntoNewBuild(Instance created)
    {
        var source = NewBuildControlsSource;

        if (!NewBuildCopySettings || source is null || ReferenceEquals(source, created))
        {
            return;
        }

        try
        {
            var sourceFile = OptionsFile.TryLoad(Path.Combine(_instances.GameDirectory(source), GameOptions.FileName));
            var targetFile = OptionsFile.TryLoad(Path.Combine(_instances.GameDirectory(created), GameOptions.FileName));

            if (sourceFile is null || targetFile is null || RunningGames.IsRunning(_instances.GameDirectory(source)))
            {
                AppendConsole($"[transfer] controls not carried into {created.Name}: options.txt unreadable or the game is running");
                return;
            }

            var plan = ControlsTransfer.Plan(sourceFile, source.VersionId, targetFile, created.VersionId);

            if (plan.Any)
            {
                WriteControls(created, source, plan);
            }
        }
        catch (Exception ex)
        {
            AppendConsole($"[transfer] controls not carried into {created.Name}: {ex.Message}");
        }
    }

    // ===================== Words for the list =====================

    private static string ControlsGroupName(ControlsGroup group) => group switch
    {
        ControlsGroup.Keys => Localize("Transfer_GroupKeys", "keys"),
        ControlsGroup.ModKeys => Localize("Transfer_GroupModKeys", "mod keys"),
        ControlsGroup.Mouse => Localize("Transfer_GroupMouse", "mouse and movement"),
        ControlsGroup.Sound => Localize("Transfer_GroupSound", "sound"),
        ControlsGroup.Chat => Localize("Transfer_GroupChat", "chat"),
        ControlsGroup.Accessibility => Localize("Transfer_GroupAccessibility", "accessibility"),
        _ => Localize("Transfer_GroupView", "view")
    };

    /// <summary>The setting's name in the player's language; a mod's key by its own words.</summary>
    public static string ControlsName(string key)
    {
        var fallback = Humanize(key);
        var resource = "Opt_" + key;
        var name = Localize(resource, fallback);

        return string.IsNullOrEmpty(name) ? fallback : name;
    }

    private static string Humanize(string key)
    {
        var text = key;

        if (text.StartsWith(ControlsTransfer.KeyPrefix, StringComparison.Ordinal))
        {
            text = text[ControlsTransfer.KeyPrefix.Length..];
        }

        if (text.StartsWith(ControlsTransfer.SoundPrefix, StringComparison.Ordinal))
        {
            text = text[ControlsTransfer.SoundPrefix.Length..];
        }

        // "key.xaero_toggle_minimap" → "xaero toggle minimap"; "iris.keybind.reload" → "iris: reload".
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries).Where(p => p is not ("key" or "keybind" or "keybinds")).ToList();
        var words = string.Join(": ", parts).Replace('_', ' ');

        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>A value as a person reads it: keys by name, volumes in percent, switches as on and off.</summary>
    public static string ControlsValue(string key, string value)
    {
        var bare = value.Trim().Trim('"');

        if (key.StartsWith(ControlsTransfer.KeyPrefix, StringComparison.Ordinal))
        {
            return KeyName(bare);
        }

        if (bare is "true" or "false")
        {
            return bare == "true" ? Localize("Transfer_On", "on") : Localize("Transfer_Off", "off");
        }

        if ((key.StartsWith(ControlsTransfer.SoundPrefix, StringComparison.Ordinal) || key is "chatOpacity" or "textBackgroundOpacity" or "mouseSensitivity") &&
            double.TryParse(bare, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction))
        {
            // The game shows sensitivity as twice the stored fraction: 0.5 is "100%".
            var percent = key == "mouseSensitivity" ? fraction * 200 : fraction * 100;
            return Math.Round(percent).ToString(CultureInfo.InvariantCulture) + " %";
        }

        if (key == "guiScale" && bare == "0")
        {
            return Localize("Transfer_GuiAuto", "auto");
        }

        return bare;
    }

    private static string KeyName(string value)
    {
        switch (value)
        {
            case "key.keyboard.unknown":
                return Localize("Transfer_KeyNone", "not set");
            case "key.mouse.left":
                return Localize("Transfer_MouseLeft", "left mouse");
            case "key.mouse.right":
                return Localize("Transfer_MouseRight", "right mouse");
            case "key.mouse.middle":
                return Localize("Transfer_MouseMiddle", "middle mouse");
        }

        if (value.StartsWith("key.mouse.", StringComparison.Ordinal))
        {
            return Localize("Transfer_MouseButton", "mouse {0}", value["key.mouse.".Length..]);
        }

        if (value.StartsWith("key.keyboard.", StringComparison.Ordinal))
        {
            var parts = value["key.keyboard.".Length..].Split('.');
            return string.Join(" ", parts.Select(p => p.Length <= 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]));
        }

        return value;
    }
}
