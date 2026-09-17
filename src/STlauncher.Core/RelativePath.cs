using System;
using System.IO;
using System.Linq;

namespace STlauncher.Core;

public static class RelativePath
{
    /// <summary>
    /// Rejects absolute paths, drive letters, and any traversal outside the target
    /// directory. Used before writing files from external metadata (modpacks, catalogs).
    /// </summary>
    public static bool IsSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Path.IsPathRooted(path) || path.Contains(':'))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length > 0 && segments.All(s => s != ".." && s != ".");
    }
}