namespace STlauncher.Core.Java;

public sealed record JavaInstallation(string ExecutablePath, int MajorVersion, string? Vendor);