using System;
using System.IO;
using System.Text.Json;
using STlauncher.Core;

namespace STlauncher.App.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;

    public SettingsService(LauncherPaths paths)
    {
        _path = Path.Combine(paths.Root, "settings.json");
    }

    /// <summary>True once the launcher has saved settings at least once: not a first run.</summary>
    public bool Exists => File.Exists(_path);

    public AppSettings Load()
    {
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

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
    }
}