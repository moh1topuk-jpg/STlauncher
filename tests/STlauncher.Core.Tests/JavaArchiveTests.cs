using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using STlauncher.Core.Java;
using Xunit;

namespace STlauncher.Core.Tests;

public class JavaArchiveTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "stlauncher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void ExtractArchive_UnpacksTarGz_StrippingTheTopFolder()
    {
        var root = TempRoot();
        var archive = Path.Combine(root, "jre.tar.gz");

        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "jdk-21.0.4+7-jre/"));
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "jdk-21.0.4+7-jre/bin/"));

            var java = new PaxTarEntry(TarEntryType.RegularFile, "jdk-21.0.4+7-jre/bin/java")
            {
                DataStream = new MemoryStream(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            };
            writer.WriteEntry(java);

            var release = new PaxTarEntry(TarEntryType.RegularFile, "jdk-21.0.4+7-jre/release")
            {
                DataStream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("JAVA_VERSION=\"21.0.4\"\n"))
            };
            writer.WriteEntry(release);
        }

        var destination = Path.Combine(root, "runtime");
        JavaManager.ExtractArchive(archive, destination);

        Assert.True(File.Exists(Path.Combine(destination, "bin", "java")));
        Assert.True(File.Exists(Path.Combine(destination, "release")));
        Assert.False(Directory.Exists(Path.Combine(destination, "jdk-21.0.4+7-jre")));

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(destination, "bin", "java")).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void ExtractArchive_RejectsEntriesOutsideTheDestination()
    {
        var root = TempRoot();
        var archive = Path.Combine(root, "evil.tar.gz");

        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../escape.txt") { DataStream = new MemoryStream(new byte[] { 1 }) });
        }

        Assert.Throws<IOException>(() => JavaManager.ExtractArchive(archive, Path.Combine(root, "runtime")));
        Assert.False(File.Exists(Path.Combine(root, "escape.txt")));

        // The zip path has the same top-folder logic and the same hole to keep shut.
        var zip = Path.Combine(root, "evil.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var e = z.CreateEntry("../escape.txt");
            using var w = e.Open();
            w.WriteByte(1);
        }

        Assert.Throws<IOException>(() => JavaManager.ExtractArchive(zip, Path.Combine(root, "runtime2")));
        Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
    }
}
