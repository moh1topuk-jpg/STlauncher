using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using STlauncher.App.Services;
using STlauncher.Core.Instances;
using STlauncher.Core.Launch;

namespace STlauncher.App.ViewModels;

/// <summary>One line of "where you played": a server or single-player, and the time there.</summary>
public sealed record PlaytimePlaceRow(string Name, string Time);

/// <summary>
/// Where the player actually is, read from what the game prints: the launch arguments
/// only know where the game was sent at start. Used for the hours per server and for the
/// Discord line. Nothing here talks to the network.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>How many places the main screen lists.</summary>
    private const int PlaytimePlacesShown = 4;

    private GameLocationTracker? _location;

    /// <summary>Starts following a run. The launcher's output pump is only listened to, never changed.</summary>
    private void BeginLocationTracking(DateTimeOffset startedAt)
    {
        EndLocationTracking();
        _location = new GameLocationTracker(startedAt);
        _gameLauncher.OutputReceived += OnLocationLine;
        _gameLauncher.ErrorReceived += OnLocationLine;
    }

    private GameLocationTracker? EndLocationTracking()
    {
        _gameLauncher.OutputReceived -= OnLocationLine;
        _gameLauncher.ErrorReceived -= OnLocationLine;

        var tracker = _location;
        _location = null;
        return tracker;
    }

    /// <summary>Called on the output threads, for every line: cheap, and never throws into the pump.</summary>
    private void OnLocationLine(string line)
    {
        try
        {
            if (_location is { } tracker && tracker.Feed(line, DateTimeOffset.Now))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsGameRunning)
                    {
                        ApplyDiscordPresence(playing: true, joinServer: false);
                    }
                });
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// True for the server this launcher is for: its address is on the site and in the
    /// catalog, so showing it gives nothing away. Any other address may be a friend's home.
    /// </summary>
    private bool IsPublicServer(string? address)
        => PublicServerAddresses().Any(known => GameLocation.SameServer(known, address));

    private IReadOnlyCollection<string> PublicServerAddresses()
    {
        var known = new List<string> { ServerDefaults.Address };

        if (_loadedCatalog is { } catalog)
        {
            known.AddRange(catalog.Builds
                .Select(b => b.ServerAddress)
                .Where(a => !string.IsNullOrWhiteSpace(a))!);
        }

        return known;
    }

    /// <summary>Adds the run's time per place to the build that was played. The caller saves it.</summary>
    private void RecordPlayPlaces(Instance instance, DateTimeOffset now)
    {
        if (EndLocationTracking() is { } tracker)
        {
            Playtime.RecordPlaces(instance, tracker.Finish(now), now);
        }
    }

    /// <summary>"Where you played": the places with the most hours in the selected build.</summary>
    public IReadOnlyList<PlaytimePlaceRow> PlaytimePlaces
        => SelectedInstance is { } instance
            ? Playtime.TopPlaces(instance, PlaytimePlacesShown)
                .Select(p => new PlaytimePlaceRow(
                    p.Address ?? Localize("Game_PlaceSinglePlayer", "Single-player"),
                    FormatPlaytime(TimeSpan.FromSeconds(p.Seconds))))
                .ToList()
            : Array.Empty<PlaytimePlaceRow>();

    public bool HasPlaytimePlaces => SelectedInstance is { } instance && instance.PlayPlaces.Any(p => p.Seconds > 0);

    public bool HasPlaytimeAllLabel => PlaytimeAllLabel.Length > 0;

    /// <summary>Whether the pill has anything to say on hover.</summary>
    public bool HasPlaytimeDetails => HasPlaytimePlaces || HasPlaytimeAllLabel;
}
