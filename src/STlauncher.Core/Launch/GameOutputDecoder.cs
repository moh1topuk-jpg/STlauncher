using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using System.Threading.Tasks;

namespace STlauncher.Core.Launch;

/// <summary>
/// Reads what the game prints as bytes and decides the encoding line by line. A modern
/// game is told to print UTF-8; an old one, or the JVM's own launcher, prints in the
/// system code page whatever it is told. Decoding the whole stream as one or the other
/// turned "C:\Users\Дмитрий" into mojibake, so each line is taken as UTF-8 when it is
/// valid UTF-8 and as the system ANSI code page when it is not.
/// </summary>
public static class GameOutputDecoder
{
    /// <summary>
    /// A line with no end is cut here: a broken stream must not grow one string without
    /// limit, and nothing the game prints on purpose is this long.
    /// </summary>
    public const int MaxLineBytes = 64 * 1024;

    private static readonly Lazy<Encoding> Ansi = new(ResolveAnsi);

    /// <summary>The system ANSI code page: what a Java older than 18 prints in by default.</summary>
    public static Encoding SystemAnsi => Ansi.Value;

    public static string Decode(ReadOnlySpan<byte> line) => Decode(line, SystemAnsi);

    /// <summary>UTF-8 when the bytes are valid UTF-8, otherwise the given code page.</summary>
    public static string Decode(ReadOnlySpan<byte> line, Encoding fallback)
    {
        if (line.IsEmpty)
        {
            return string.Empty;
        }

        // Plain ASCII is valid UTF-8, so the common case is one pass and no exception.
        return Utf8.IsValid(line) ? Encoding.UTF8.GetString(line) : fallback.GetString(line);
    }

    /// <summary>
    /// Reads the stream to its end and hands over every line, without its line break. A
    /// read error ends the pump quietly: the process is gone or the pipe is, and either
    /// way there is nothing more to read.
    /// </summary>
    public static Task PumpAsync(Stream stream, Action<string> onLine)
        => PumpAsync(stream, onLine, SystemAnsi);

    public static async Task PumpAsync(Stream stream, Action<string> onLine, Encoding fallback)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (onLine is null)
        {
            throw new ArgumentNullException(nameof(onLine));
        }

        var buffer = new byte[8192];
        var pending = new byte[1024];
        var length = 0;

        void Emit()
        {
            var end = length;

            if (end > 0 && pending[end - 1] == (byte)'\r')
            {
                end--;
            }

            var text = Decode(pending.AsSpan(0, end), fallback);
            length = 0;

            try
            {
                onLine(text);
            }
            catch (Exception)
            {
                // A subscriber must not end the stream for everyone after it.
            }
        }

        while (true)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(), default).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];

                // 0x0A is a line break in UTF-8 and in every ANSI code page alike: it is
                // never part of a longer character, so splitting on the byte is safe.
                if (value == (byte)'\n')
                {
                    Emit();
                    continue;
                }

                if (length == pending.Length)
                {
                    if (length >= MaxLineBytes)
                    {
                        Emit();
                    }
                    else
                    {
                        Array.Resize(ref pending, Math.Min(pending.Length * 2, MaxLineBytes));
                    }
                }

                pending[length++] = value;
            }
        }

        // The last line of a process that died mid-sentence has no line break.
        if (length > 0)
        {
            Emit();
        }
    }

    private static Encoding ResolveAnsi()
    {
        try
        {
            var codePage = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? (int)GetACP()
                : CultureInfo.CurrentCulture.TextInfo.ANSICodePage;

            // "Beta: use Unicode UTF-8 for worldwide language support" makes the ANSI page
            // UTF-8 itself. Latin-1 then: it maps every byte to something and never throws.
            if (codePage is 65001 or <= 0)
            {
                return Encoding.Latin1;
            }

            // Asked of the provider directly, not registered: registering would change
            // what Encoding.GetEncoding returns for the whole launcher.
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.Latin1;
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
