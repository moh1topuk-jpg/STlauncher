using System.Collections.Generic;
using STlauncher.Core.Loaders;

namespace STlauncher.Core.Modpacks;

public sealed record ModpackFilePlan(
    string RelativePath,
    string Url,
    string? Sha1,
    string? Sha512,
    long Size);

public sealed record ModpackPlan(
    string Name,
    string VersionId,
    string? GameVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    IReadOnlyList<ModpackFilePlan> Files,
    bool HasOverrides,
    string OverridesFolder = "overrides");