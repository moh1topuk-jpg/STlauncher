namespace STlauncher.Core.Http;

public sealed record DownloadProgress(
    int Completed,
    int Total,
    long BytesDownloaded,
    int Failed,
    string? CurrentFile)
{
    public double Fraction => Total == 0 ? 0 : (double)Completed / Total;
}