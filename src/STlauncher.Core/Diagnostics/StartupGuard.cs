using System;
using System.IO;
using System.Text.Json;

namespace STlauncher.Core.Diagnostics;

/// <summary>How far a start of the launcher got.</summary>
public enum StartupState
{
    /// <summary>No record, or one that could not be read.</summary>
    Unknown,

    /// <summary>The process began and has not reported anything since.</summary>
    Starting,

    /// <summary>The main window became usable.</summary>
    Ready,

    /// <summary>A generous time passed and the main window was still not usable.</summary>
    Stalled,

    /// <summary>The launcher was closed in order before it got ready: the player's choice, not a failure.</summary>
    Closed,

    /// <summary>The "reinstall the files?" question is on screen.</summary>
    Asking,

    /// <summary>The player agreed and the files are being put back.</summary>
    Repairing
}

/// <param name="Failed">The previous start of this same version never got ready.</param>
/// <param name="AlreadyRepaired">This version's files were already reinstalled once at the player's word.</param>
public readonly record struct PreviousStart(bool Failed, bool AlreadyRepaired);

/// <summary>
/// Remembers, in one small file in the data root, whether the last start of the launcher
/// reached a usable window. The next start reads it and may offer to reinstall the files.
/// </summary>
/// <remarks>
/// The file is written at the very beginning of a start and flushed to disk, so a process
/// that is killed, or dies before it can say anything, leaves "starting" behind: that is
/// the record of the failure. A start that the player closed in order says so, and so does
/// one that is busy asking the question, so neither is mistaken for a failure and the
/// question cannot follow itself in a loop.
/// </remarks>
public sealed class StartupGuard
{
    public const string FileName = "startup-state.json";

    private readonly string _path;
    private readonly object _gate = new();

    private string _version = string.Empty;
    private StartupState _state = StartupState.Unknown;
    private string? _repairedVersion;

    public StartupGuard(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public static StartupGuard In(string dataRoot) => new(Path.Combine(dataRoot, FileName));

    /// <summary>The state this start is in, as last written.</summary>
    public StartupState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Reads what the previous start left behind. Call once, before any Mark: the marks
    /// overwrite it. A start of another version is never held against this one.
    /// </summary>
    public PreviousStart ReadPrevious(string version)
    {
        lock (_gate)
        {
            _version = version ?? string.Empty;

            var (previousVersion, previousState, repaired) = Load();
            _repairedVersion = repaired;

            var sameVersion = _version.Length > 0 &&
                              string.Equals(previousVersion, _version, StringComparison.OrdinalIgnoreCase);

            return new PreviousStart(
                Failed: sameVersion && (previousState is StartupState.Starting or StartupState.Stalled),
                AlreadyRepaired: _version.Length > 0 &&
                                 string.Equals(repaired, _version, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The question is being shown. A launcher killed now is not a failed start.</summary>
    public void MarkAsking() => Move(_ => StartupState.Asking);

    /// <summary>
    /// The player pressed the button and the package is about to be applied. From here on
    /// this version counts as reinstalled once.
    /// </summary>
    public void MarkRepairing()
    {
        lock (_gate)
        {
            _repairedVersion = _version;
        }

        Move(_ => StartupState.Repairing);
    }

    /// <summary>The usual start begins: from now until Ready the start is "not ready yet".</summary>
    public void MarkStarting() => Move(_ => StartupState.Starting);

    /// <summary>The main window is usable. A start that was slow but arrived is not a failure.</summary>
    public void MarkReady() => Move(state =>
        state is StartupState.Starting or StartupState.Stalled ? StartupState.Ready : state);

    /// <summary>The generous time is up. Only a start that is still waiting can stall.</summary>
    public void MarkStalled() => Move(state =>
        state == StartupState.Starting ? StartupState.Stalled : state);

    /// <summary>
    /// The launcher is closing in order. A start that had already stalled stays stalled:
    /// closing a window that never came up is exactly the case being recorded.
    /// </summary>
    public void MarkClosed() => Move(state =>
        state == StartupState.Starting ? StartupState.Closed : state);

    private void Move(Func<StartupState, StartupState> transition)
    {
        lock (_gate)
        {
            var next = transition(_state);

            if (next == _state && next != StartupState.Repairing)
            {
                return;
            }

            _state = next;
            Save();
        }
    }

    private (string? Version, StartupState State, string? RepairedVersion) Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return (null, StartupState.Unknown, null);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var root = document.RootElement;

            var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            var repaired = root.TryGetProperty("repairedVersion", out var r) ? r.GetString() : null;
            var state = root.TryGetProperty("state", out var s) &&
                        Enum.TryParse<StartupState>(s.GetString(), ignoreCase: true, out var parsed)
                ? parsed
                : StartupState.Unknown;

            return (version, state, repaired);
        }
        catch (Exception)
        {
            // An unreadable record is no record: better to miss one offer than to make a false one.
            return (null, StartupState.Unknown, null);
        }
    }

    private void Save()
    {
        try
        {
            using var stream = new MemoryStream();

            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("version", _version);
                writer.WriteString("state", _state.ToString().ToLowerInvariant());
                writer.WriteString("at", DateTimeOffset.Now.ToString("O"));

                if (!string.IsNullOrEmpty(_repairedVersion))
                {
                    writer.WriteString("repairedVersion", _repairedVersion);
                }

                writer.WriteEndObject();
            }

            // AtomicFile flushes to disk before the swap, which is what lets the record
            // outlive a hard kill a moment later.
            AtomicFile.Write(_path, target => target.Write(stream.GetBuffer(), 0, (int)stream.Length));
        }
        catch (Exception)
        {
            // A read-only or full disk must not stop the launcher from starting.
        }
    }
}
