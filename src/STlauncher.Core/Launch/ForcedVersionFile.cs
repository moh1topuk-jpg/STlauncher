using System;
using System.IO;

namespace STlauncher.Core.Launch;

/// <summary>
/// A version JSON taken away for "re-download the game files", kept beside itself until
/// the version is known to come back. A JSON of Mojang's is fetched again and the copy
/// goes; one that only exists on this computer - a profile imported from another
/// launcher - is put back.
/// </summary>
public static class ForcedVersionFile
{
    public const string Suffix = ".set-aside";

    /// <summary>Moves the file to its set-aside name; null when there is nothing to move.</summary>
    public static string? SetAside(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var aside = path + Suffix;
            File.Move(path, aside, overwrite: true);
            return aside;
        }
        catch (Exception)
        {
            // Left where it is: the launch goes on with the file it had.
            return null;
        }
    }

    /// <summary>Puts the file back unless a new one has taken its place meanwhile.</summary>
    public static void Restore(string aside, string path)
    {
        try
        {
            if (File.Exists(aside) && !File.Exists(path))
            {
                File.Move(aside, path);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>The version came back by itself: the old copy is not needed.</summary>
    public static void Discard(string? aside)
    {
        try
        {
            if (aside is not null && File.Exists(aside))
            {
                File.Delete(aside);
            }
        }
        catch (Exception)
        {
        }
    }
}
