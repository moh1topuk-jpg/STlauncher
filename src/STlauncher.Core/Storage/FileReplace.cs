using System;
using System.IO;
using System.IO.Compression;

namespace STlauncher.Core.Storage;

/// <summary>
/// Puts a file in place of another without opening the old one for writing. A mod or a
/// pack in a build may be one of several names of a single file on disk (see
/// <see cref="SharedFileStore"/>): <c>File.Copy(overwrite: true)</c> and
/// <c>ExtractToFile(overwrite: true)</c> truncate and refill the existing file, and that
/// would change it in every other build that shares it. Writing a new file beside it and
/// renaming over replaces only this build's name.
/// </summary>
public static class FileReplace
{
    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/>, replacing what is there.</summary>
    public static void Copy(string source, string target)
        => Write(target, temp => File.Copy(source, temp, overwrite: false));

    /// <summary>Unpacks a zip entry to <paramref name="target"/>, replacing what is there.</summary>
    public static void Extract(ZipArchiveEntry entry, string target)
        => Write(target, temp => entry.ExtractToFile(temp, overwrite: false));

    private static void Write(string target, Action<string> writeNew)
    {
        if (!File.Exists(target))
        {
            writeNew(target);
            return;
        }

        var temp = $"{target}.{Guid.NewGuid():N}.part";

        try
        {
            writeNew(temp);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
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
        catch (Exception)
        {
            // The failure that brought us here is the one to report.
        }
    }
}
