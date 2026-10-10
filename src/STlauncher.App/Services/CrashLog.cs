using System;
using System.IO;
using System.Runtime.InteropServices;
using STlauncher.Core;

namespace STlauncher.App.Services;

/// <summary>
/// The launcher's own crash log: failures that no try/catch in the interface saw. Every
/// entry says which launcher on which system wrote it, because the file outlives updates
/// and a stack without a version sends the reader to the wrong source.
/// </summary>
public static class CrashLog
{
    public const string FileName = "crash.log";

    /// <summary>
    /// The version built into this executable. Known without Velopack, the data folder or
    /// the interface, so it is there even when the crash is in one of those.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    public static string OperatingSystem { get; } =
        $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    /// <summary>
    /// The data root, where the support report looks for the log. A broken pointer file
    /// must not cost the entry, so the default folder is the fallback.
    /// </summary>
    public static string DataRoot
    {
        get
        {
            try
            {
                return DataLocation.Resolve();
            }
            catch (Exception)
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "STlauncher");
            }
        }
    }

    /// <param name="origin">Which net caught it: "unhandled", "unobserved task", and so on.</param>
    public static void Write(Exception? exception, string origin)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var directory = DataRoot;
            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, FileName),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] STlauncher {Version}, {OperatingSystem}, {origin}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing left to try - a failing crash logger must not become the crash.
        }
    }

    private static string ReadVersion()
    {
        try
        {
            return typeof(CrashLog).Assembly.GetName().Version?.ToString(3) ?? "dev";
        }
        catch (Exception)
        {
            return "dev";
        }
    }
}
