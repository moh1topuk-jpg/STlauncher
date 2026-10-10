using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace STlauncher.Core.Storage;

/// <summary>
/// Which file on the disk a path names. Two paths with the same identity are two names
/// of one file: hard links.
/// </summary>
/// <param name="Links">How many names the file has, this one included.</param>
public readonly record struct FileIdentity(uint Volume, ulong Index, int Links)
{
    public bool IsSameFile(FileIdentity other) => Volume == other.Volume && Index == other.Index;
}

/// <summary>
/// Hard links, which .NET 8 has no managed call for. Everything here answers "no" rather
/// than throwing: a disk that cannot link (FAT32, exFAT, another volume, a network share)
/// is a normal place to keep the game, and the caller then keeps plain files as before.
/// </summary>
public static class HardLink
{
    /// <summary>Makes <paramref name="newPath"/> a second name of <paramref name="existingPath"/>.</summary>
    /// <returns>False when the link was not made, for any reason, including a name already taken.</returns>
    public static bool TryCreate(string existingPath, string newPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateHardLinkW(Extended(newPath), Extended(existingPath), IntPtr.Zero);
            }

            return UnixLink(existingPath, newPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The file behind a path and how many names it has. Null where the system has no
    /// way to say that this class knows (macOS, an old Linux): callers then fall back to
    /// comparing contents.
    /// </summary>
    public static FileIdentity? TryGetIdentity(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return LinuxIdentity(path);
            }

            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            // Sharing everything: a jar the running game holds open must still answer.
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (!GetFileInformationByHandle(handle, out var info))
            {
                return null;
            }

            return new FileIdentity(
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
                (int)info.NumberOfLinks);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The Win32 call stops at 260 characters unless told otherwise, and a mod three
    /// folders deep in a long user name gets there.
    /// </summary>
    private static string Extended(string path)
    {
        var full = Path.GetFullPath(path);

        if (full.Length < 240 || full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return full;
        }

        return @"\\?\" + full;
    }

    private static bool UnixLink(string existingPath, string newPath)
    {
        try
        {
            return link(existingPath, newPath) == 0;
        }
        catch (DllNotFoundException)
        {
            // A Linux without the development symlink has no "libc.so", only the real name.
            return linkGlibc(existingPath, newPath) == 0;
        }
    }

    /// <summary>
    /// statx(2): unlike stat, its structure is the same on every architecture and C
    /// library, so it can be read here without a native shim.
    /// </summary>
    private static FileIdentity? LinuxIdentity(string path)
    {
        const int CurrentDirectory = -100;      // AT_FDCWD
        const int DoNotFollowLinks = 0x100;     // AT_SYMLINK_NOFOLLOW
        const uint LinkCount = 0x4;             // STATX_NLINK
        const uint Inode = 0x100;               // STATX_INO

        var buffer = new byte[256];
        int result;

        try
        {
            result = statx(CurrentDirectory, path, DoNotFollowLinks, LinkCount | Inode, buffer);
        }
        catch (DllNotFoundException)
        {
            result = statxGlibc(CurrentDirectory, path, DoNotFollowLinks, LinkCount | Inode, buffer);
        }

        // The kernel says which fields it filled; a file system that keeps no link count
        // or inode must not be trusted to have one.
        if (result != 0 || (BitConverter.ToUInt32(buffer, 0) & (LinkCount | Inode)) != (LinkCount | Inode))
        {
            return null;
        }

        var links = BitConverter.ToUInt32(buffer, 16);
        var inode = BitConverter.ToUInt64(buffer, 32);
        var deviceMajor = BitConverter.ToUInt32(buffer, 136);
        var deviceMinor = BitConverter.ToUInt32(buffer, 140);

        return new FileIdentity((deviceMajor << 20) | (deviceMinor & 0xFFFFF), inode, (int)links);
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(
        int directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        [Out] byte[] buffer);

    [DllImport("libc.so.6", EntryPoint = "statx", SetLastError = true)]
    private static extern int statxGlibc(
        int directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mask,
        [Out] byte[] buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport("libc.so.6", EntryPoint = "link", SetLastError = true)]
    private static extern int linkGlibc(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string existingPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    /// <summary>BY_HANDLE_FILE_INFORMATION, the three FILETIMEs spelled as their halves.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
