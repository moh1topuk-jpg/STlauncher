using System;
using System.IO;
using STlauncher.Core.Nbt;

namespace STlauncher.Core.Worlds;

public enum WorldGameMode
{
    Survival = 0,
    Creative = 1,
    Adventure = 2,
    Spectator = 3
}

public enum WorldDifficulty
{
    Peaceful = 0,
    Easy = 1,
    Normal = 2,
    Hard = 3
}

/// <summary>
/// A single-player world as its level.dat describes it. Everything here is read from that
/// one small file; what takes real disk work - the size of the folder - is measured
/// separately, after the list is already on screen.
/// </summary>
/// <param name="Directory">The world's folder, full path.</param>
/// <param name="FolderName">The folder's name: what the game and the backups know the world by.</param>
/// <param name="Name">LevelName, or the folder name when level.dat could not be read.</param>
/// <param name="IsReadable">False when level.dat is damaged. The world is still listed, so it can be exported or removed.</param>
/// <param name="GameVersion">The version it was last opened in ("1.21.4"); null before 1.9, which did not record it.</param>
/// <param name="LastPlayed">From level.dat; for an unreadable world, when the file was last written.</param>
/// <param name="IconPath">icon.png inside the world, when the game has made one.</param>
public sealed record WorldInfo(
    string Directory,
    string FolderName,
    string Name,
    bool IsReadable,
    WorldGameMode? GameMode = null,
    bool IsHardcore = false,
    bool? AllowCheats = null,
    WorldDifficulty? Difficulty = null,
    string? GameVersion = null,
    long? Seed = null,
    DateTimeOffset? LastPlayed = null,
    string? IconPath = null)
{
    public const string LevelFileName = "level.dat";

    public const string IconFileName = "icon.png";

    public const string SessionLockFileName = "session.lock";

    public string LevelPath => Path.Combine(Directory, LevelFileName);

    /// <summary>Reads a world folder. Never throws for a damaged level.dat: that is a state, not an error.</summary>
    public static WorldInfo Read(string worldDirectory)
    {
        var directory = Path.GetFullPath(worldDirectory);
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var levelPath = Path.Combine(directory, LevelFileName);
        var iconPath = Path.Combine(directory, IconFileName);
        var icon = File.Exists(iconPath) ? iconPath : null;

        try
        {
            var data = NbtFile.Read(levelPath).Root.GetCompound("Data")
                       ?? throw new InvalidDataException("level.dat has no Data tag.");

            return FromData(data, directory, folder, icon);
        }
        catch (Exception)
        {
            return new WorldInfo(directory, folder, folder, IsReadable: false, LastPlayed: WrittenAt(levelPath), IconPath: icon);
        }
    }

    private static WorldInfo FromData(NbtCompound data, string directory, string folder, string? icon)
    {
        var name = data.GetString("LevelName");

        // Where the seed lives has moved once already: beside the other settings up to
        // 1.15, inside the world generation settings since 1.16.
        var seed = data.GetCompound("WorldGenSettings")?.GetLong("seed") ?? data.GetLong("RandomSeed");

        // Difficulty and hardcore are plain tags in most versions; a nested block is
        // looked at too, so a layout that groups them does not read as "unknown".
        var nested = data.GetCompound("difficulty_settings");
        var hardcore = (data.GetByte("hardcore") ?? nested?.GetByte("hardcore")) is 1;

        WorldDifficulty? difficulty = data.GetByte("Difficulty") is { } level and >= 0 and <= 3
            ? (WorldDifficulty)level
            : nested?.GetString("difficulty") is { } text && Enum.TryParse<WorldDifficulty>(text, ignoreCase: true, out var parsed)
                ? parsed
                : null;

        WorldGameMode? mode = data.GetInt("GameType") is { } type and >= 0 and <= 3 ? (WorldGameMode)type : null;

        DateTimeOffset? lastPlayed = null;

        // Zero is what a world that was created and never entered carries.
        if (data.GetLong("LastPlayed") is { } millis and > 0)
        {
            try
            {
                lastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(millis);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return new WorldInfo(
            directory,
            folder,
            string.IsNullOrWhiteSpace(name) ? folder : name,
            IsReadable: true,
            mode,
            hardcore,
            data.GetByte("allowCommands") is { } cheats ? cheats == 1 : null,
            difficulty,
            data.GetCompound("Version")?.GetString("Name"),
            seed,
            lastPlayed ?? WrittenAt(Path.Combine(directory, LevelFileName)),
            icon);
    }

    private static DateTimeOffset? WrittenAt(string path)
    {
        try
        {
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>A world the player deleted: it sits in the launcher's trash inside the build until it is brought back or its time runs out.</summary>
/// <param name="FolderName">The name the world's folder had in saves.</param>
public sealed record TrashedWorld(string Directory, string FolderName, string Name, DateTimeOffset DeletedAt)
{
    public DateTimeOffset ExpiresAt => DeletedAt + WorldManager.TrashRetention;
}

/// <summary>One zip of one world in the build's backups area.</summary>
public sealed record WorldBackupInfo(string Path, string FileName, string FolderName, DateTimeOffset CreatedAt, long Size);

public enum WorldBusyReason
{
    /// <summary>The build's game is running; it owns the saves folder until it exits.</summary>
    GameRunning,

    /// <summary>session.lock is held: the world is open in a game right now.</summary>
    WorldOpen
}

/// <summary>Thrown instead of touching a world the game is using.</summary>
public sealed class WorldBusyException : IOException
{
    public WorldBusyException(WorldBusyReason reason)
        : base(reason == WorldBusyReason.GameRunning
            ? "The game is running; worlds are left alone until it exits."
            : "The world is open in the game.")
    {
        Reason = reason;
    }

    public WorldBusyReason Reason { get; }
}

/// <summary>Thrown when a zip offered as a world is not one, or is not safe to unpack.</summary>
public sealed class WorldArchiveException : IOException
{
    public WorldArchiveException(WorldArchiveProblem problem, string message)
        : base(message)
    {
        Problem = problem;
    }

    public WorldArchiveProblem Problem { get; }
}

public enum WorldArchiveProblem
{
    /// <summary>No level.dat at the top or inside a single folder.</summary>
    NotAWorld,

    /// <summary>An entry would land outside the world's folder.</summary>
    UnsafePath,

    /// <summary>Unpacks into more than the launcher accepts, or more than it said it would.</summary>
    TooLarge
}
