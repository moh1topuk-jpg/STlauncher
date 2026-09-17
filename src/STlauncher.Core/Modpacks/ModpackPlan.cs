using System.Collections.Generic;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Modpacks;

public enum ModpackFormat
{
    Modrinth,
    CurseForge
}

public sealed record ModpackFilePlan(
    string RelativePath,
    string? Url,
    string? Sha1,
    string? Sha512,
    long Size,
    int? ProjectId = null,
    int? FileId = null,
    bool Required = true);

public sealed record ModpackPlan(
    ModpackFormat Format,
    string Name,
    string VersionId,
    string? GameVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    IReadOnlyList<ModpackFilePlan> Files,
    bool HasOverrides,
    string OverridesFolder = "overrides");