using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Instances;

namespace STlauncher.Core.Mods;

/// <summary>Which version of which project a file is, asked by SHA-1. Modrinth answers this for a whole folder at once.</summary>
public interface IModHashLookup
{
    Task<IReadOnlyDictionary<string, ModVersion>> GetVersionsByHashesAsync(
        IEnumerable<string> sha1Hashes,
        CancellationToken cancellationToken = default);
}

/// <summary>The same question in CurseForge's terms: by <see cref="CurseForgeFingerprint"/>.</summary>
public interface IModFingerprintLookup
{
    Task<IReadOnlyDictionary<uint, ModVersion>> MatchFingerprintsAsync(
        IEnumerable<uint> fingerprints,
        CancellationToken cancellationToken = default);
}

/// <summary>A jar in the build the launcher has no origin for.</summary>
/// <param name="FileName">As it is on disk, ".disabled" included.</param>
public sealed record UnidentifiedFile(string FileName, string Path)
{
    /// <summary>The file's SHA-1 when the caller has already computed it.</summary>
    public string? Sha1 { get; init; }
}

/// <summary>A file that turned out to be a known version of a known project.</summary>
public sealed record IdentifiedFile(string FileName, ModProject Project, ModVersion Version)
{
    public ModSource Source => Version.Source;

    /// <summary>
    /// The record that says so, in the shape an install from that source writes: the slug
    /// in Id, and for CurseForge the two numbers that find the file again. The file keeps
    /// the name the player gave it, and what the player did to it (switched it off) stays.
    /// </summary>
    public InstalledModRecord ToRecord(InstalledModRecord? existing, string folder = ModManager.ModsFolderName)
        => new()
        {
            FileName = FileName,
            Source = Source,
            Id = Project.Slug.Length > 0 ? Project.Slug : Project.Id,
            Name = Project.Title,
            IconUrl = Project.IconUrl,
            Version = Version.VersionNumber,
            Folder = existing?.Folder ?? folder,
            Required = existing?.Required ?? false,
            DisabledByUser = existing?.DisabledByUser ?? false,
            ProjectId = Source == ModSource.CurseForge ? Version.ProjectId ?? Project.Id : null,
            FileId = Source == ModSource.CurseForge ? Version.Id : null
        };
}

/// <summary>
/// Finds out what the jars a player dropped into a build are. Modrinth is asked first, by
/// SHA-1, for all of them in one request; what it does not know goes to CurseForge by
/// fingerprint. Only reads the files, and decides nothing about them: the caller stamps
/// the launcher's own records with the answers.
/// </summary>
public sealed class ModFileRecognizer
{
    /// <summary>Project pages asked for at once. A hundred unknown jars is a hundred pages, once.</summary>
    private const int ProjectLookups = 4;

    private readonly IModHashLookup _modrinthHashes;
    private readonly IModSource _modrinth;
    private readonly IModFingerprintLookup? _curseForgeFingerprints;
    private readonly IModSource? _curseForge;

    public ModFileRecognizer(
        IModHashLookup modrinthHashes,
        IModSource modrinth,
        IModFingerprintLookup? curseForgeFingerprints = null,
        IModSource? curseForge = null)
    {
        _modrinthHashes = modrinthHashes ?? throw new ArgumentNullException(nameof(modrinthHashes));
        _modrinth = modrinth ?? throw new ArgumentNullException(nameof(modrinth));
        _curseForgeFingerprints = curseForgeFingerprints;
        _curseForge = curseForge;
    }

    /// <summary>
    /// True for a file with no record, or with the record a drop makes: no source and no
    /// project. A file from the catalog, a modpack or either site is already known.
    /// </summary>
    public static bool NeedsIdentifying(InstalledModRecord? record)
        => record is null || (record.Source == ModSource.Manual && string.IsNullOrEmpty(record.Id));

    /// <param name="knownModrinth">Modrinth's answer for these files' hashes when the caller already has it; saves the request.</param>
    /// <param name="useCurseForge">False while CurseForge is not available: the second pass is skipped.</param>
    public async Task<IReadOnlyList<IdentifiedFile>> IdentifyAsync(
        IReadOnlyList<UnidentifiedFile> files,
        IReadOnlyDictionary<string, ModVersion>? knownModrinth = null,
        bool useCurseForge = true,
        CancellationToken cancellationToken = default)
    {
        var found = new List<IdentifiedFile>();

        if (files.Count == 0)
        {
            return found;
        }

        // Hashing a folder of jars is disk work.
        var hashed = await Task.Run(
            () => files
                .Select(f => (File: f, Sha1: f.Sha1 ?? ModManager.TryComputeSha1(f.Path)))
                .Where(p => p.Sha1 is not null)
                .ToList(),
            cancellationToken).ConfigureAwait(false);

        var left = files.ToList();

        // Pass one: Modrinth.
        try
        {
            var versions = knownModrinth ??
                           await _modrinthHashes.GetVersionsByHashesAsync(hashed.Select(p => p.Sha1!), cancellationToken).ConfigureAwait(false);

            var matched = hashed
                .Where(p => versions.TryGetValue(p.Sha1!, out var version) && !string.IsNullOrEmpty(version.ProjectId))
                .Select(p => (p.File, Version: versions[p.Sha1!]))
                .ToList();

            var projects = await LoadProjectsAsync(_modrinth, matched.Select(m => m.Version.ProjectId!), cancellationToken).ConfigureAwait(false);

            foreach (var (file, version) in matched)
            {
                // Without the project there is no slug to mark the file by; next time, then.
                if (projects.TryGetValue(version.ProjectId!, out var project))
                {
                    found.Add(new IdentifiedFile(file.FileName, project, version));
                    left.Remove(file);
                }
                else
                {
                    // Modrinth knows the file: it is not CurseForge's to claim.
                    left.Remove(file);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Modrinth could not be asked. CurseForge is the second opinion, not the first:
            // a file both sites carry would be filed under the wrong one.
            return found;
        }

        // Pass two: CurseForge, for what is left.
        if (!useCurseForge || _curseForgeFingerprints is null || _curseForge is null || left.Count == 0)
        {
            return found;
        }

        try
        {
            var printed = await Task.Run(
                () => left
                    .Select(f => (File: f, Print: CurseForgeFingerprint.TryComputeFile(f.Path)))
                    .Where(p => p.Print is not null)
                    .ToList(),
                cancellationToken).ConfigureAwait(false);

            if (printed.Count == 0)
            {
                return found;
            }

            var matches = await _curseForgeFingerprints
                .MatchFingerprintsAsync(printed.Select(p => p.Print!.Value), cancellationToken)
                .ConfigureAwait(false);

            var matched = printed
                .Where(p => matches.TryGetValue(p.Print!.Value, out var version) && !string.IsNullOrEmpty(version.ProjectId))
                .Select(p => (p.File, Version: matches[p.Print!.Value]))
                .ToList();

            var projects = await LoadProjectsAsync(_curseForge, matched.Select(m => m.Version.ProjectId!), cancellationToken).ConfigureAwait(false);

            foreach (var (file, version) in matched)
            {
                if (projects.TryGetValue(version.ProjectId!, out var project))
                {
                    found.Add(new IdentifiedFile(file.FileName, project, version with { Source = ModSource.CurseForge }));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // What Modrinth recognised still stands.
        }

        return found;
    }

    private static async Task<Dictionary<string, ModProject>> LoadProjectsAsync(
        IModSource source,
        IEnumerable<string> projectIds,
        CancellationToken cancellationToken)
    {
        var ids = projectIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, ModProject>(StringComparer.OrdinalIgnoreCase);

        using var gate = new SemaphoreSlim(ProjectLookups);

        var pages = await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return (Id: id, Project: await source.GetProjectAsync(id, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return (Id: id, Project: (ModProject?)null);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        foreach (var (id, project) in pages)
        {
            if (project is not null)
            {
                result[id] = project;
            }
        }

        return result;
    }
}
