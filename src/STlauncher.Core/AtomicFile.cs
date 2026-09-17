using System;
using System.IO;
using System.Text;

namespace STlauncher.Core;

/// <summary>
/// Writes files without ever leaving a half-written one behind.
/// </summary>
/// <remarks>
/// Every settings/definition file the launcher owns is read back on the next start, and
/// the readers treat a parse failure as "no data". A plain <c>File.WriteAllText</c> that is
/// interrupted (crash, power loss, full disk) therefore does not just lose the write - it
/// loses whatever the file held before, silently. Writing to a temporary file and swapping
/// it in makes the replacement atomic.
/// </remarks>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
        => Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(false), leaveOpen: true);
            writer.Write(contents);
            writer.Flush();
        });

    public static void WriteAllLines(string path, IEnumerable<string> lines, Encoding? encoding = null)
        => Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(false), leaveOpen: true);
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }

            writer.Flush();
        });

    public static void Write(string path, Action<Stream> write)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";

        try
        {
            using (var stream = File.Create(temp))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            // Never leave the temporary file lying around to confuse the next run.
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
            // Best effort only - the original exception is the one that matters.
        }
    }
}
