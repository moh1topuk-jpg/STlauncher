using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using STlauncher.Core.Launch;
using STlauncher.Core.Storage;

namespace STlauncher.Core.Instances;

public enum BuildStateKind
{
    /// <summary>The game ran on exactly these mods and closed without an error.</summary>
    Working,

    /// <summary>Taken before a launch on mods that have not worked yet; becomes Working if the game does.</summary>
    Pending,

    /// <summary>What the build was just before a restore: the restore's own undo.</summary>
    BeforeRestore
}

/// <param name="Path">Relative to the game folder, with "/": "mods/sodium.jar", "config/iris.properties".</param>
/// <param name="Blob">The kept copy's name in the states folder.</param>
/// <param name="Linked">The kept copy is a second name of the build's file, not a copy of it.</param>
public sealed record BuildStateFile(string Path, long Size, DateTime WriteUtc, string Blob, bool Linked);

/// <summary>One remembered state of a build's mods and their settings.</summary>
public sealed class BuildState
{
    public string Id { get; set; } = string.Empty;

    public BuildStateKind Kind { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>When the game last ran well on it; null while it is only pending.</summary>
    public DateTime? WorkedUtc { get; set; }

    /// <summary>What the mods folder was, as one string: two states with the same one have the same mods.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public List<BuildStateFile> Files { get; set; } = new();

    /// <summary>Settings files that were left out (too big): a restore leaves them alone.</summary>
    public List<string> Skipped { get; set; } = new();

    /// <summary>The caller's own description of the build at the time (its settings, its records); opaque here.</summary>
    public string? Definition { get; set; }

    /// <summary>The moment that matters to the player: when it worked, or when it was taken.</summary>
    [JsonIgnore]
    public DateTime WhenUtc => WorkedUtc ?? CreatedUtc;

    [JsonIgnore]
    public IEnumerable<BuildStateFile> Mods => Files.Where(f => BuildStates.IsModPath(f.Path));

    /// <summary>Mods that were switched on.</summary>
    [JsonIgnore]
    public int ModCount => Mods.Count(f => BuildStates.IsEnabledMod(f.Path));
}

public enum BuildChangeKind
{
    /// <summary>In the build now, not then: moved aside by a restore.</summary>
    Added,

    /// <summary>In the build then, gone now: brought back.</summary>
    Removed,

    /// <summary>Under the same name, a different file now: the old one comes back.</summary>
    Changed
}

public sealed record BuildStateChange(BuildChangeKind Kind, string Path);

/// <summary>What a restore to <see cref="Target"/> would do, worked out before anything moves.</summary>
public sealed record BuildStatePlan(BuildState Target, IReadOnlyList<BuildStateChange> Mods, IReadOnlyList<BuildStateChange> Config)
{
    public bool Any => Mods.Count > 0 || Config.Count > 0;
}

/// <param name="AsideFolder">Where the files that left the build lie, relative to the game folder.</param>
/// <param name="Undo">The state the build was in just before: restoring it undoes this restore.</param>
/// <param name="Failed">Files that could not be brought back (their kept copy is gone or changed).</param>
public sealed record BuildRestoreResult(int MovedAside, int Restored, string AsideFolder, BuildState? Undo, IReadOnlyList<string> Failed);

public enum BuildStateError
{
    /// <summary>The build's game is running: its jars are open and its settings are being written.</summary>
    GameRunning,

    /// <summary>The state could not be kept (no room for copies); nothing was changed.</summary>
    CannotKeep,

    /// <summary>A file of the build could not be moved aside; everything moved so far was put back.</summary>
    MoveAside
}

/// <summary>A restore that did not happen. The build is as it was.</summary>
public sealed class BuildStateException : IOException
{
    public BuildStateException(BuildStateError error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    public BuildStateError Error { get; }
}

/// <summary>
/// "Put the build back as it was when it worked." Players add mods on top of the server's
/// build, and most crashes come from those; this keeps the last few states of a build's
/// mods and their settings that the game actually ran on, and puts one back on request.
/// </summary>
/// <remarks>
/// <para>
/// Light on purpose. A state is a small manifest plus one kept copy per file, in
/// <c>.stlauncher/states/</c> inside the build. A mod's kept copy is a hard link to the
/// mod - a second name of the same file, no disk space - and a file that did not change
/// between two states is kept once for both. Where the disk cannot link (FAT32, exFAT) the
/// mods are copied, within a budget. Settings files are always copied: the game writes into
/// them, and a link would change the kept copy along with the live one. Mods are never
/// written into (see <see cref="FileReplace"/>), so a link stays what it was; a kept copy
/// whose size or date no longer matches its manifest is not used.
/// </para>
/// <para>
/// Only <c>mods/</c> (the files directly in it, as the loaders read it) and <c>config/</c>
/// are covered. Worlds, screenshots, options.txt, servers.dat, resource and shader packs
/// are never looked at, never moved and never restored.
/// </para>
/// <para>
/// A restore deletes nothing: files that came after the state are moved to
/// <c>.stlauncher/replaced/&lt;date&gt;-restore/</c>, as the mod replacer does, and the state
/// the build was in just before is kept first, so the restore can itself be undone.
/// </para>
/// </remarks>
public sealed class BuildStates
{
    public const string StatesDirectory = ".stlauncher/states";
    public const int KeepWorking = 3;

    /// <summary>A settings file bigger than this is a cache or a database, not a setting.</summary>
    public const long MaxConfigFile = 4L * 1024 * 1024;

    /// <summary>All the settings of one state together.</summary>
    public const long MaxConfigBytes = 64L * 1024 * 1024;

    /// <summary>What one state may copy of mods where links do not work.</summary>
    public const long DefaultCopyBudget = 1024L * 1024 * 1024;

    private const string BlobsName = "blobs";
    private const string DisabledSuffix = ".disabled";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Func<string, string, bool> _link;
    private readonly Func<string, bool> _isRunning;
    private readonly Func<DateTime> _now;

    /// <param name="link">(existing, new name) → linked; tests pass one that refuses, as a FAT disk does.</param>
    /// <param name="isRunning">Whether the game of this folder runs; the launcher's own knowledge by default.</param>
    public BuildStates(
        string gameDirectory,
        Func<string, string, bool>? link = null,
        Func<string, bool>? isRunning = null,
        Func<DateTime>? utcNow = null)
    {
        GameDirectory = Path.GetFullPath(gameDirectory ?? throw new ArgumentNullException(nameof(gameDirectory)));
        _link = link ?? HardLink.TryCreate;
        _isRunning = isRunning ?? RunningGames.IsRunning;
        _now = utcNow ?? (() => DateTime.UtcNow);
    }

    public string GameDirectory { get; }

    public long CopyBudget { get; init; } = DefaultCopyBudget;

    private string Root => Path.Combine(GameDirectory, StatesDirectory.Replace('/', Path.DirectorySeparatorChar));

    private string Blobs => Path.Combine(Root, BlobsName);

    // ------------------------------------------------------------------ what is there

    internal static bool IsModPath(string path) => path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsEnabledMod(string path)
        => IsModPath(path) && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    /// <summary>A mod file's name without ".disabled".</summary>
    public static string BaseName(string fileName)
        => fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^DisabledSuffix.Length] : fileName;

    private sealed record LiveFile(string Path, string FullPath, long Size, DateTime WriteUtc);

    /// <summary>The build's mods and settings as they are now.</summary>
    private (List<LiveFile> Files, List<string> Skipped) Scan()
    {
        var files = new List<LiveFile>();
        var skipped = new List<string>();

        var mods = Path.Combine(GameDirectory, "mods");

        if (Directory.Exists(mods))
        {
            foreach (var info in new DirectoryInfo(mods).EnumerateFiles())
            {
                // Hidden names are the launcher's own staging files; ".part" a download under way.
                if (info.Name.StartsWith('.') || info.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(new LiveFile("mods/" + info.Name, info.FullName, info.Length, info.LastWriteTimeUtc));
            }
        }

        var config = Path.Combine(GameDirectory, "config");

        if (Directory.Exists(config))
        {
            long total = 0;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

            foreach (var info in new DirectoryInfo(config).EnumerateFiles("*", options).OrderBy(f => f.FullName, StringComparer.Ordinal))
            {
                var relative = "config/" + Path.GetRelativePath(config, info.FullName).Replace(Path.DirectorySeparatorChar, '/');

                if (info.Length > MaxConfigFile || total + info.Length > MaxConfigBytes)
                {
                    skipped.Add(relative);
                    continue;
                }

                total += info.Length;
                files.Add(new LiveFile(relative, info.FullName, info.Length, info.LastWriteTimeUtc));
            }
        }

        return (files, skipped);
    }

    private static string FingerprintOf(IEnumerable<(string Path, long Size, DateTime WriteUtc)> files)
    {
        var text = new StringBuilder();

        foreach (var file in files.Where(f => IsModPath(f.Path)).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(file.Path.ToLowerInvariant()).Append('|').Append(file.Size).Append('|').Append(Seconds(file.WriteUtc)).Append('\n');
        }

        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>File times to the second: FAT keeps two, and a copy may round.</summary>
    private static long Seconds(DateTime utc) => utc.Ticks / TimeSpan.TicksPerSecond / 2;

    /// <summary>The mods of the build now, as one string.</summary>
    public string CurrentFingerprint()
        => FingerprintOf(Scan().Files.Select(f => (f.Path, f.Size, f.WriteUtc)));

    // ------------------------------------------------------------------ the states

    /// <summary>Every kept state, newest first.</summary>
    public IReadOnlyList<BuildState> All()
    {
        if (!Directory.Exists(Root))
        {
            return Array.Empty<BuildState>();
        }

        var states = new List<BuildState>();

        foreach (var path in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<BuildState>(File.ReadAllText(path), JsonOptions) is { } state &&
                    state.Id == Path.GetFileNameWithoutExtension(path) &&
                    state.Files.All(f => IsSane(f)))
                {
                    states.Add(state);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return states.OrderByDescending(s => s.WhenUtc).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The states the game worked on, newest first: what the player can go back to.</summary>
    public IReadOnlyList<BuildState> Working() => All().Where(s => s.Kind == BuildStateKind.Working).ToList();

    /// <summary>The state the last restore left behind, to undo it.</summary>
    public BuildState? LastUndo() => All().FirstOrDefault(s => s.Kind == BuildStateKind.BeforeRestore);

    /// <summary>
    /// The newest state the game worked on, when the mods are different now: what a crash
    /// card offers to go back to. Null when there is none, or the mods are the same.
    /// </summary>
    public BuildState? Offer()
    {
        var latest = Working().FirstOrDefault();
        return latest is not null && latest.Fingerprint != CurrentFingerprint() ? latest : null;
    }

    /// <summary>
    /// Before a launch. When the mods differ from the last state that worked, they are
    /// kept as a pending state; the launch's outcome decides whether it becomes one.
    /// </summary>
    /// <returns>The pending state, or null when the mods are those of the last working state.</returns>
    public BuildState? BeforeLaunch(string? definition = null)
    {
        DeleteAll(BuildStateKind.Pending);

        var latest = Working().FirstOrDefault();

        if (latest is not null && latest.Fingerprint == CurrentFingerprint())
        {
            return null;
        }

        return Capture(BuildStateKind.Pending, definition);
    }

    /// <summary>After the game closed. Worked: the pending state is one to come back to.</summary>
    /// <param name="definition">The build's settings now; replaces the one kept when the game worked.</param>
    public void AfterLaunch(bool worked, string? definition = null)
    {
        var pending = All().FirstOrDefault(s => s.Kind == BuildStateKind.Pending);

        if (!worked)
        {
            DeleteAll(BuildStateKind.Pending);
            Prune();
            return;
        }

        if (pending is not null)
        {
            pending.Kind = BuildStateKind.Working;
            pending.WorkedUtc = _now();
            pending.Definition = definition ?? pending.Definition;
            Save(pending);
        }
        else if (Working().FirstOrDefault() is { } latest && latest.Fingerprint == CurrentFingerprint())
        {
            latest.WorkedUtc = _now();
            latest.Definition = definition ?? latest.Definition;
            Save(latest);
        }

        Prune();
    }

    /// <summary>
    /// Keeps the build as it is now. Mods are linked (or copied, within <see cref="CopyBudget"/>),
    /// settings copied; a file kept already for an earlier state is not kept twice.
    /// </summary>
    /// <exception cref="BuildStateException">The copies would not fit the budget; nothing was kept.</exception>
    public BuildState Capture(BuildStateKind kind, string? definition = null)
    {
        var (live, skipped) = Scan();
        Directory.CreateDirectory(Blobs);

        var state = new BuildState
        {
            Id = NewId(),
            Kind = kind,
            CreatedUtc = _now(),
            Definition = definition,
            Skipped = skipped
        };

        long copied = 0;
        var made = new List<string>();

        try
        {
            foreach (var file in live)
            {
                var blob = BlobName(file.Path, file.Size, file.WriteUtc);
                var blobPath = Path.Combine(Blobs, blob);
                var isMod = IsModPath(file.Path);

                if (File.Exists(blobPath) && Matches(blobPath, file.Size, file.WriteUtc))
                {
                    state.Files.Add(new BuildStateFile(file.Path, file.Size, file.WriteUtc, blob, isMod && IsLinked(file.FullPath, blobPath)));
                    continue;
                }

                TryDelete(blobPath);

                if (isMod && _link(file.FullPath, blobPath))
                {
                    made.Add(blobPath);
                    state.Files.Add(new BuildStateFile(file.Path, file.Size, file.WriteUtc, blob, true));
                    continue;
                }

                if (isMod)
                {
                    copied += file.Size;

                    if (copied > CopyBudget)
                    {
                        throw new BuildStateException(BuildStateError.CannotKeep, "The build's mods do not fit the space set aside for copies.");
                    }
                }

                File.Copy(file.FullPath, blobPath, overwrite: false);
                File.SetLastWriteTimeUtc(blobPath, file.WriteUtc);
                made.Add(blobPath);
                state.Files.Add(new BuildStateFile(file.Path, file.Size, file.WriteUtc, blob, false));
            }
        }
        catch
        {
            foreach (var path in made)
            {
                TryDelete(path);
            }

            throw;
        }

        state.Fingerprint = FingerprintOf(state.Files.Select(f => (f.Path, f.Size, f.WriteUtc)));
        Save(state);
        return state;
    }

    /// <summary>What restoring <paramref name="target"/> would change. Touches nothing.</summary>
    public BuildStatePlan Plan(BuildState target)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        var live = Scan().Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var then = target.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var skipped = new HashSet<string>(target.Skipped, StringComparer.OrdinalIgnoreCase);

        var changes = new List<BuildStateChange>();

        foreach (var file in live.Values)
        {
            if (!then.TryGetValue(file.Path, out var old))
            {
                if (!skipped.Contains(file.Path))
                {
                    changes.Add(new BuildStateChange(BuildChangeKind.Added, file.Path));
                }
            }
            else if (old.Size != file.Size || Seconds(old.WriteUtc) != Seconds(file.WriteUtc))
            {
                changes.Add(new BuildStateChange(BuildChangeKind.Changed, file.Path));
            }
        }

        foreach (var old in then.Values.Where(f => !live.ContainsKey(f.Path)))
        {
            changes.Add(new BuildStateChange(BuildChangeKind.Removed, old.Path));
        }

        var ordered = changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();

        return new BuildStatePlan(
            target,
            ordered.Where(c => IsModPath(c.Path)).ToList(),
            ordered.Where(c => !IsModPath(c.Path)).ToList());
    }

    /// <summary>
    /// Puts the build back as <paramref name="target"/> was. Keeps the present state first
    /// (the undo), then moves what came after aside and brings back what was there.
    /// </summary>
    /// <param name="definition">The build's settings now, kept with the undo.</param>
    /// <exception cref="BuildStateException">The game runs, the undo could not be kept, or a file would not move. Nothing changed.</exception>
    public BuildRestoreResult Restore(BuildState target, string? definition = null)
    {
        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        if (_isRunning(GameDirectory))
        {
            throw new BuildStateException(BuildStateError.GameRunning, "The game of this build is running.");
        }

        var plan = Plan(target);

        if (!plan.Any)
        {
            return new BuildRestoreResult(0, 0, string.Empty, null, Array.Empty<string>());
        }

        // The way back from the way back, before anything moves.
        DeleteAll(BuildStateKind.BeforeRestore);
        var undo = Capture(BuildStateKind.BeforeRestore, definition);

        var stamp = _now().ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var asideRelative = $"{ReplacedRoot}/{stamp}-restore";
        var aside = Path.Combine(GameDirectory, asideRelative.Replace('/', Path.DirectorySeparatorChar));

        for (var n = 2; Directory.Exists(aside); n++)
        {
            asideRelative = $"{ReplacedRoot}/{stamp}-restore-{n}";
            aside = Path.Combine(GameDirectory, asideRelative.Replace('/', Path.DirectorySeparatorChar));
        }

        var all = plan.Mods.Concat(plan.Config).ToList();
        var moved = new List<(string From, string To)>();

        try
        {
            foreach (var change in all.Where(c => c.Kind is BuildChangeKind.Added or BuildChangeKind.Changed))
            {
                var from = Full(change.Path);
                var to = Path.Combine(aside, change.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to, overwrite: false);
                moved.Add((from, to));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (from, to) in Enumerable.Reverse(moved))
            {
                try
                {
                    File.Move(to, from, overwrite: false);
                }
                catch (Exception)
                {
                    // Left where it is, in the folder named by the exception's message.
                }
            }

            throw new BuildStateException(BuildStateError.MoveAside, ex.Message, ex);
        }

        var then = target.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var restored = 0;
        var failed = new List<string>();

        foreach (var change in all.Where(c => c.Kind is BuildChangeKind.Removed or BuildChangeKind.Changed))
        {
            var file = then[change.Path];
            var blobPath = Path.Combine(Blobs, file.Blob);
            var destination = Full(change.Path);

            try
            {
                if (!File.Exists(blobPath) || !Matches(blobPath, file.Size, file.WriteUtc))
                {
                    failed.Add(change.Path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // A mod comes back as a name of its kept file; a setting as a copy of its own,
                // so the game writing into it does not change what is kept.
                if (!(IsModPath(change.Path) && _link(blobPath, destination)))
                {
                    File.Copy(blobPath, destination, overwrite: false);
                    File.SetLastWriteTimeUtc(destination, file.WriteUtc);
                }

                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(change.Path);
            }
        }

        Prune();
        return new BuildRestoreResult(moved.Count, restored, asideRelative, undo, failed);
    }

    /// <summary>
    /// What a state costs on disk: its copies, and linked files nothing else names any more
    /// (a mod deleted from the build since). Links to files the build still has cost nothing.
    /// </summary>
    public long CostOf(BuildState state)
    {
        long bytes = 0;

        foreach (var blob in state.Files.Select(f => f.Blob).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(Blobs, blob);

            try
            {
                var info = new FileInfo(path);

                if (!info.Exists)
                {
                    continue;
                }

                var linked = state.Files.First(f => f.Blob == blob).Linked;

                if (!linked || HardLink.TryGetIdentity(path) is not { Links: > 1 })
                {
                    bytes += info.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return bytes;
    }

    /// <summary>
    /// The last <see cref="KeepWorking"/> working states, one pending and one undo; the
    /// kept copies no remaining state names go.
    /// </summary>
    public void Prune()
    {
        var all = All();
        var extra = all.Where(s => s.Kind == BuildStateKind.Working).Skip(KeepWorking)
            .Concat(all.Where(s => s.Kind == BuildStateKind.Pending).Skip(1))
            .Concat(all.Where(s => s.Kind == BuildStateKind.BeforeRestore).Skip(1))
            .ToList();

        foreach (var state in extra)
        {
            TryDelete(ManifestPath(state.Id));
        }

        SweepBlobs();
    }

    // ------------------------------------------------------------------ helpers

    private static readonly string ReplacedRoot = Mods.ModFileReplacer.ReplacedDirectory;

    private string Full(string relative) => Path.Combine(GameDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    private string ManifestPath(string id) => Path.Combine(Root, id + ".json");

    private string NewId()
    {
        var baseId = _now().ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var id = baseId;

        for (var n = 2; File.Exists(ManifestPath(id)); n++)
        {
            id = $"{baseId}-{n}";
        }

        return id;
    }

    private void Save(BuildState state)
    {
        Directory.CreateDirectory(Root);
        var path = ManifestPath(state.Id);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private void DeleteAll(BuildStateKind kind)
    {
        foreach (var state in All().Where(s => s.Kind == kind))
        {
            TryDelete(ManifestPath(state.Id));
        }
    }

    private void SweepBlobs()
    {
        if (!Directory.Exists(Blobs))
        {
            return;
        }

        var used = new HashSet<string>(All().SelectMany(s => s.Files).Select(f => f.Blob), StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(Blobs))
        {
            if (!used.Contains(Path.GetFileName(path)))
            {
                TryDelete(path);
            }
        }
    }

    /// <summary>Content-free name: the same file at the same size and date is kept once.</summary>
    private static string BlobName(string path, long size, DateTime writeUtc)
    {
        var key = $"{path.ToLowerInvariant()}|{size}|{Seconds(writeUtc)}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return hash + (IsModPath(path) ? ".mod" : ".cfg");
    }

    private static bool Matches(string path, long size, DateTime writeUtc)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == size && Seconds(info.LastWriteTimeUtc) == Seconds(writeUtc);
    }

    private static bool IsLinked(string first, string second)
        => HardLink.TryGetIdentity(first) is { } a && HardLink.TryGetIdentity(second) is { } b && a.IsSameFile(b);

    /// <summary>The manifest is a file in the build's folder: a path in it may only point inside mods/ or config/.</summary>
    private static bool IsSane(BuildStateFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.Blob) ||
            file.Blob.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Blob.StartsWith('.'))
        {
            return false;
        }

        var parts = file.Path.Split('/');

        if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            return false;
        }

        return (parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase) && parts.Length == 2) ||
               (parts[0].Equals("config", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
