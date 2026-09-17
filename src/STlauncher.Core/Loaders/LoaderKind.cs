namespace STlauncher.Core.Loaders;

public enum LoaderKind
{
    Vanilla,
    Fabric,
    Quilt,
    Forge,
    NeoForge
}

public sealed record LoaderVersion(string Version, bool Stable);