using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace STlauncher.Relay;

/// <summary>
/// Shared builds: a player leaves a build code (two kilobytes and up) and gets ten
/// characters to send to a friend; the friend's launcher asks for the text back. Unlike
/// an invite, which lives in memory for as long as its room, a shared build is kept on
/// disk, because the one who shared it will have closed the launcher long before the
/// friend gets round to it.
///
/// On the same port, in the same style - one ASCII line at a time:
///   SHARE BEGIN           answered OK, or ERR NOSHARE (this relay keeps no builds) or
///                         ERR LIMIT (too many from this address this hour).
///   SHARE + chunk         the text, up to 200 characters a line. Not answered.
///   SHARE END             answered SHARED code, or ERR TOOBIG, ERR BADDATA (not a build
///                         code), ERR FULL (no room left), ERR FAILED (the disk refused).
///   GETSHARE code         answered "OK lines" and then that many lines of text, exactly
///                         as FETCH answers; or ERR NOTFOUND (no such code, or it has
///                         expired), ERR LIMIT, ERR NOSHARE.
///
/// A relay from before this answers the first line with ERR BADCMD and hangs up, which
/// is how a launcher tells that it has to hand out the long code instead.
///
/// What may be left is a build code and nothing else: the "STB1." prefix and base64url
/// characters. The relay does not look inside, but it will not keep arbitrary text for
/// arbitrary people.
/// </summary>
public sealed partial class RelayServer
{
    /// <summary>What every build code starts with; the launcher's BuildCode.Prefix.</summary>
    private const string BuildCodePrefix = "STB1.";

    /// <summary>How much of a shared build travels in one line, either way.</summary>
    private const int ShareChunk = 200;

    /// <summary>The whole of an upload has this long, however slowly its lines come.</summary>
    private static readonly TimeSpan ShareUploadTime = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ShareSweepEvery = TimeSpan.FromDays(1);

    private ShareStore? _shares;
    private HourlyLimit? _shareLimit;
    private HourlyLimit? _shareFetchLimit;

    /// <summary>How many shared builds are kept right now; zero when the relay keeps none.</summary>
    public int SharedBuilds => _shares?.Count ?? 0;

    private void StartShares()
    {
        if (string.IsNullOrWhiteSpace(_options.ShareDirectory))
        {
            return;
        }

        var store = new ShareStore(_options.ShareDirectory, _options.ShareMaxTotalBytes, _options.ShareMaxCount, _options.ShareLife);

        try
        {
            var expired = store.Load();
            _log?.Invoke($"shares: {store.Count} kept, {store.TotalBytes} bytes, {expired} expired and removed");
        }
        catch (Exception ex)
        {
            // The tunnels do not need the disk. A relay that cannot keep builds still
            // carries friends to each other and answers ERR NOSHARE to the rest.
            _log?.Invoke($"shares: off, the directory cannot be used ({ex.GetType().Name})");
            return;
        }

        _shares = store;
        _shareLimit = new HourlyLimit(_options.SharesPerHour);
        _shareFetchLimit = new HourlyLimit(_options.ShareFetchesPerHour);
        _ = Task.Run(() => SweepSharesAsync(store));
    }

    private async Task SweepSharesAsync(ShareStore store)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ShareSweepEvery, _stop.Token).ConfigureAwait(false);

                var expired = store.Sweep();
                _log?.Invoke($"shares: {store.Count} kept, {store.TotalBytes} bytes, {expired} expired and removed");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeShareAsync(Socket socket, IPAddress gateKey)
    {
        if (_shares is not { } store)
        {
            await SendLineAsync(socket, "ERR NOSHARE").ConfigureAwait(false);
            return;
        }

        if (!_shareLimit!.TryTake(gateKey))
        {
            await SendLineAsync(socket, "ERR LIMIT").ConfigureAwait(false);
            return;
        }

        if (!await SendLineAsync(socket, "OK").ConfigureAwait(false))
        {
            return;
        }

        var max = _options.ShareMaxBytes;
        var text = new StringBuilder();
        string? refusal = null;

        // Past the limit the rest is still read, a little way, and thrown away: hanging
        // up on a client in the middle of its sending would reset the connection and
        // take the reason with it.
        var linesLeft = (max / ShareChunk * 2) + 64;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limit.CancelAfter(ShareUploadTime);
        var reader = new LineReader(socket, _options.MaxLineLength);

        while (true)
        {
            var line = await reader.ReadAsync(limit.Token).ConfigureAwait(false);

            if (line is null || --linesLeft < 0)
            {
                return;
            }

            if (line == "SHARE END")
            {
                break;
            }

            if (!line.StartsWith("SHARE + ", StringComparison.Ordinal) || line.Length == 8 || line.Length - 8 > ShareChunk)
            {
                return;
            }

            if (refusal is not null)
            {
                continue;
            }

            if (text.Length + line.Length - 8 > max)
            {
                refusal = "ERR TOOBIG";
                text.Clear();
                continue;
            }

            text.Append(line, 8, line.Length - 8);
        }

        if (refusal is null && !IsBuildCode(text))
        {
            refusal = "ERR BADDATA";
        }

        if (refusal is not null)
        {
            await SendLineAsync(socket, refusal).ConfigureAwait(false);
            return;
        }

        var outcome = store.Put(text.ToString(), out var code);

        switch (outcome)
        {
            case ShareOutcome.Stored:
            case ShareOutcome.Refreshed:
                // The first four characters tell two shares apart in the journal and are
                // a long way from the code itself.
                _log?.Invoke($"share {(outcome == ShareOutcome.Stored ? "new" : "same")} {code[..4]} bytes={text.Length}");
                await SendLineAsync(socket, "SHARED " + code).ConfigureAwait(false);
                break;

            case ShareOutcome.Full:
                _log?.Invoke("share refused: full");
                await SendLineAsync(socket, "ERR FULL").ConfigureAwait(false);
                break;

            default:
                _log?.Invoke("share refused: the disk did not take it");
                await SendLineAsync(socket, "ERR FAILED").ConfigureAwait(false);
                break;
        }
    }

    private async Task ServeGetShareAsync(Socket socket, IPAddress gateKey, string codeText)
    {
        if (_shares is not { } store)
        {
            await SendLineAsync(socket, "ERR NOSHARE").ConfigureAwait(false);
            return;
        }

        if (!_shareFetchLimit!.TryTake(gateKey))
        {
            await SendLineAsync(socket, "ERR LIMIT").ConfigureAwait(false);
            return;
        }

        var code = codeText.ToUpperInvariant();
        var payload = ShareStore.IsCode(code) ? store.Get(code) : null;

        if (payload is null)
        {
            // The same wait whether the code never existed, has expired or is not even
            // shaped like one: a guesser learns nothing and gets nowhere fast.
            try
            {
                await Task.Delay(_options.ShareMissDelay, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _log?.Invoke("getshare miss");
            await SendLineAsync(socket, "ERR NOTFOUND").ConfigureAwait(false);
            return;
        }

        _log?.Invoke($"getshare {code[..4]} bytes={payload.Length}");

        var lines = (payload.Length + ShareChunk - 1) / ShareChunk;
        var answer = new StringBuilder(payload.Length + lines + 16);
        answer.Append("OK ").Append(lines.ToString(CultureInfo.InvariantCulture));

        for (var at = 0; at < payload.Length; at += ShareChunk)
        {
            answer.Append('\n').Append(payload, at, Math.Min(ShareChunk, payload.Length - at));
        }

        // A big build is a thousand lines, and a send each would be a thousand packets;
        // one send for the lot would give a slow line five seconds for all of it. So:
        // a few dozen lines at a time, each piece with the usual time to get through.
        var whole = answer.ToString();
        const int Piece = 16 * 1024;

        for (var at = 0; at < whole.Length; at += Piece)
        {
            var last = at + Piece >= whole.Length;
            var bytes = Encoding.ASCII.GetBytes(whole.Substring(at, Math.Min(Piece, whole.Length - at)) + (last ? "\n" : string.Empty));

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            limit.CancelAfter(SendTimeout);

            var sent = 0;

            while (sent < bytes.Length)
            {
                sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, limit.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>"STB1." and then base64url, as the launcher writes a build. Nothing else is kept.</summary>
    private static bool IsBuildCode(StringBuilder text)
    {
        if (text.Length <= BuildCodePrefix.Length)
        {
            return false;
        }

        for (var i = 0; i < BuildCodePrefix.Length; i++)
        {
            if (text[i] != BuildCodePrefix[i])
            {
                return false;
            }
        }

        var seen = 0;

        foreach (var chunk in text.GetChunks())
        {
            foreach (var c in chunk.Span)
            {
                if (seen++ >= BuildCodePrefix.Length && !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Lines off a connection that carries nothing but lines, read a buffer at a time.
    /// The handshake reader takes single bytes so as not to swallow the start of a
    /// tunnel; an upload of a quarter of a megabyte has no tunnel behind it and would
    /// cost a quarter of a million reads that way.
    /// </summary>
    private sealed class LineReader
    {
        private readonly Socket _socket;
        private readonly byte[] _buffer = new byte[8 * 1024];
        private readonly byte[] _line;
        private int _at;
        private int _filled;

        public LineReader(Socket socket, int maxLineLength)
        {
            _socket = socket;
            _line = new byte[maxLineLength];
        }

        /// <summary>The next line, or null: closed, too long, not printable ASCII, or out of time.</summary>
        public async Task<string?> ReadAsync(CancellationToken token)
        {
            var length = 0;

            while (true)
            {
                if (_at == _filled)
                {
                    try
                    {
                        _filled = await _socket.ReceiveAsync(_buffer.AsMemory(), SocketFlags.None, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }

                    _at = 0;

                    if (_filled == 0)
                    {
                        return null;
                    }
                }

                var value = _buffer[_at++];

                if (value == '\n')
                {
                    return Encoding.ASCII.GetString(_line, 0, length);
                }

                if (value == '\r')
                {
                    continue;
                }

                if (value < 0x20 || value > 0x7E || length == _line.Length)
                {
                    return null;
                }

                _line[length++] = value;
            }
        }
    }

    /// <summary>
    /// So many per address per hour. Like the connection gate, it lives in memory only and
    /// forgets an address once its hour is over.
    /// </summary>
    private sealed class HourlyLimit
    {
        private static readonly long Window = Stopwatch.Frequency * 3600;

        private readonly int _max;
        private readonly Dictionary<IPAddress, Entry> _entries = new();
        private long _lastSweep = Stopwatch.GetTimestamp();

        public HourlyLimit(int max)
        {
            _max = max;
        }

        public bool TryTake(IPAddress key)
        {
            var now = Stopwatch.GetTimestamp();

            lock (_entries)
            {
                if (now - _lastSweep >= Window)
                {
                    _lastSweep = now;

                    foreach (var (address, old) in _entries.ToArray())
                    {
                        if (now - old.WindowStart >= Window)
                        {
                            _entries.Remove(address);
                        }
                    }
                }

                if (!_entries.TryGetValue(key, out var entry) || now - entry.WindowStart >= Window)
                {
                    entry = new Entry { WindowStart = now };
                    _entries[key] = entry;
                }

                if (entry.Taken >= _max)
                {
                    return false;
                }

                entry.Taken++;
                return true;
            }
        }

        private sealed class Entry
        {
            public int Taken;
            public long WindowStart;
        }
    }
}
