using System;
using System.IO;
using System.Text.Json;
using STlauncher.Core;
using STlauncher.Core.Storage;

namespace STlauncher.App.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// How long a change waits for the ones that come with it. Short enough that pulling
    /// the plug loses next to nothing, long enough that a start, a build switch or a
    /// dragged slider is one write and not twenty.
    /// </summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly string _path;
    private readonly CoalescedWrite _writer;

    /// <summary>What the last read found, handed to the next reader: a start reads the settings in three places.</summary>
    private AppSettings? _loaded;

    public SettingsService(LauncherPaths paths)
    {
        _path = Path.Combine(paths.Root, "settings.json");
        _writer = new CoalescedWrite(Write, SaveDelay);
        _writer.Failed += ex => SaveFailed?.Invoke(ex);
    }

    /// <summary>A delayed save could not be written. Raised on a pool thread.</summary>
    public event Action<Exception>? SaveFailed;

    /// <summary>True once the launcher has saved settings at least once: not a first run.</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>
    /// The settings as they are on disk. The answer is kept until the next save, so the
    /// readers of one start share a single read; none of them changes what they are given.
    /// </summary>
    public AppSettings Load()
    {
        if (_loaded is { } known)
        {
            return known;
        }

        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

            if (settings is not null)
            {
                // A save of exactly this text is not a save. Only when the file is in the
                // shape this version writes: an older file gains its new fields on the
                // first save as before.
                _writer.Seed(json);
                _loaded = settings;
                return settings;
            }

            // Valid JSON that produced nothing usable - same treatment as a syntax error.
            PreserveCorruptFile();
        }
        catch (JsonException)
        {
            // A single bad character used to wipe nicknames, the Java path and the language
            // silently, and the next save overwrote the file. Keep the damaged copy first.
            PreserveCorruptFile();
        }
        catch (IOException)
        {
            // Locked by another instance - use defaults for this session but leave the
            // file alone; it is probably fine and will be readable next time.
            return new AppSettings();
        }

        return new AppSettings();
    }

    /// <summary>Moves a damaged settings file aside so it can be recovered by hand.</summary>
    private void PreserveCorruptFile()
    {
        try
        {
            var backup = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_path, backup, overwrite: false);
        }
        catch (Exception)
        {
            // Best effort: never let the recovery attempt itself break startup.
        }
    }

    /// <summary>Writes the settings now, on this thread, and anything still waiting with them.</summary>
    public void Save(AppSettings settings)
    {
        SaveSoon(settings);
        Flush();
    }

    /// <summary>
    /// Writes the settings shortly, off this thread, together with whatever else is asked
    /// for in the meantime. <paramref name="settings"/> must not be changed afterwards.
    /// </summary>
    public void SaveSoon(AppSettings settings)
    {
        _loaded = null;
        _writer.Request(JsonSerializer.Serialize(settings, Options));
    }

    /// <summary>Puts a waiting save on disk before going on: at exit, on hiding to the tray, before a game start.</summary>
    public void Flush() => _writer.Flush();

    /// <summary>The data folder has moved: nothing more is written into the old one.</summary>
    public void StopWriting() => _writer.Discard();

    private void Write(string json)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(_path, json);
    }
}
