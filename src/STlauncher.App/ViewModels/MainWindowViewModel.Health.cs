using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using STlauncher.Core.Diagnostics;

namespace STlauncher.App.ViewModels;

/// <summary>
/// The launcher watching itself: what it was doing, kept where the freeze watchdog can
/// read it from another thread, and the freeze reports themselves for the support zip.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>How far back a freeze still counts for the anonymous usage report.</summary>
    private static readonly TimeSpan FreezeCountWindow = TimeSpan.FromDays(7);

    private readonly Dictionary<string, Func<MainWindowViewModel, bool>?> _busyFlagReaders = new(StringComparer.Ordinal);
    private ActivitySnapshot? _activity;

    /// <summary>
    /// Mirrors the section and every "busy" flag into the snapshot as they change. The
    /// flags are found by name (Is…Busy, Is…ing), so a feature added later is covered
    /// without anyone remembering this file.
    /// </summary>
    public void TrackActivity(ActivitySnapshot activity)
    {
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        activity.SetSection(Section.ToString(), DateTimeOffset.Now);
        PropertyChanged += OnActivityPropertyChanged;
    }

    private void OnActivityPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_activity is null || e.PropertyName is not { Length: > 2 } name)
        {
            return;
        }

        if (name == nameof(Section))
        {
            _activity.SetSection(Section.ToString(), DateTimeOffset.Now);
            return;
        }

        if (!IsBusyFlagName(name))
        {
            return;
        }

        if (!_busyFlagReaders.TryGetValue(name, out var reader))
        {
            reader = CreateFlagReader(name);
            _busyFlagReaders[name] = reader;
        }

        if (reader is not null)
        {
            _activity.SetFlag(name, reader(this), DateTimeOffset.Now);
        }
    }

    /// <summary>IsBusy, IsBrowserBusy, IsInstalling, IsGameRunning…: a long operation in progress.</summary>
    internal static bool IsBusyFlagName(string name)
        => name.StartsWith("Is", StringComparison.Ordinal) &&
           (name.EndsWith("Busy", StringComparison.Ordinal) || name.EndsWith("ing", StringComparison.Ordinal));

    /// <summary>Looked up once per flag; after that a change costs one delegate call.</summary>
    private static Func<MainWindowViewModel, bool>? CreateFlagReader(string name)
    {
        try
        {
            var getter = typeof(MainWindowViewModel)
                .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?
                .GetMethod;

            return getter is not null && getter.ReturnType == typeof(bool)
                ? getter.CreateDelegate<Func<MainWindowViewModel, bool>>()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private FreezeReportStore FreezeReports => FreezeReportStore.In(_paths.Root);

    /// <summary>
    /// Freezes over the last week, as a bare number: the only thing about them that may
    /// leave the machine without the player sending a report by hand.
    /// </summary>
    private int RecentFreezeCount()
    {
        try
        {
            return FreezeReports.CountSince(DateTimeOffset.Now - FreezeCountWindow);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>The kept freeze reports, into the support zip under freezes/.</summary>
    private void AddFreezeReports(ZipArchive zip)
    {
        foreach (var path in FreezeReports.List())
        {
            AddTail(zip, path, "freezes/" + Path.GetFileName(path));
        }
    }
}
