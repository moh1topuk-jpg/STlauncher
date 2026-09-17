namespace STlauncher.Core.Http;

public sealed record DownloadItem(
    string Url,
    string DestinationPath,
    string? Sha1 = null,
    long Size = 0,
    string? Sha256 = null,
    string? Sha512 = null);