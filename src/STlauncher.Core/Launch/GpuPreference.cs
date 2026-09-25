using System;
using System.IO;

namespace STlauncher.Core.Launch;

/// <summary>
/// Windows decides per executable which graphics card runs it. A laptop with two cards
/// starts Java on the power-saving one unless someone says otherwise in the graphics
/// settings, and few players know that page exists. This writes the same choice Windows
/// would: "high performance" for the Java the game runs on, under the current user.
/// </summary>
public static class GpuPreference
{
    public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>The value Windows stores for "high performance" in its own settings page.</summary>
    public const string HighPerformance = "GpuPreference=2;";

    /// <summary>
    /// Marks the executable for the high-performance card. Returns true when the value was
    /// written now, false when it was already there or the platform has no such setting.
    /// </summary>
    public static bool TryPreferHighPerformance(string executablePath, out string? error)
    {
        error = null;

        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathRooted(executablePath))
        {
            return false;
        }

        try
        {
            var path = Path.GetFullPath(executablePath);

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);

            if (key is null)
            {
                error = "registry key unavailable";
                return false;
            }

            if (key.GetValue(path) is string existing && existing.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            key.SetValue(path, HighPerformance);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
