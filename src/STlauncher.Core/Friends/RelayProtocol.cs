using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Friends;

/// <summary>Where a relay listens.</summary>
public sealed record RelayEndpoint(string Host, int Port)
{
    /// <summary>The port the relay program listens on unless told otherwise.</summary>
    public const int DefaultPort = 25580;

    /// <summary>Reads "host:port" or a bare host, which gets the default port.</summary>
    public static bool TryParse(string? text, out RelayEndpoint? endpoint)
    {
        endpoint = null;

        if (!HostPort.TryParse(text, out var host, out var port))
        {
            return false;
        }

        endpoint = new RelayEndpoint(host, port ?? DefaultPort);
        return true;
    }

    public override string ToString() => HostPort.Format(Host, Port);
}

/// <summary>Why the relay way did not work, for the page to put into words.</summary>
public enum RelayFailure
{
    None,

    /// <summary>No relay address in the catalog and none in the environment.</summary>
    NotConfigured,

    /// <summary>The relay did not answer: it is down, or the network between is.</summary>
    RelayUnreachable,

    /// <summary>The relay answered that it is at a limit: rooms, guests in the room, or connections from this address.</summary>
    RelayBusy,

    /// <summary>The relay is there but the host's room is not, or the host did not pick the connection up.</summary>
    HostOffline,

    /// <summary>The relay refused the request outright: a different protocol version, or not a relay at all.</summary>
    Rejected
}

/// <summary>
/// The two keys of a room. The host key is the host's secret: it opens the room and
/// picks guests up. The room key is derived from it and goes into the invite: it lets a
/// friend join and nothing more, so an invite that leaks cannot be used to take the room
/// over.
/// </summary>
public static class RelayKeys
{
    private const int KeyLength = 32;

    /// <summary>128 random bits as 32 hex digits.</summary>
    public static string NewHostKey()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(KeyLength / 2)).ToLowerInvariant();

    /// <summary>The first half of SHA-256 over the host key's bytes. The relay computes the same.</summary>
    public static string RoomKeyFor(string hostKey)
    {
        if (!IsKey(hostKey))
        {
            throw new ArgumentException("A host key is 32 hex digits.", nameof(hostKey));
        }

        var hash = SHA256.HashData(Convert.FromHexString(hostKey));
        return Convert.ToHexString(hash, 0, KeyLength / 2).ToLowerInvariant();
    }

    /// <summary>
    /// The host key of the server's second room, the one with a public port. Kept apart
    /// from the invite room so that switching the public address on or off never drops
    /// the friends who came by invite, and derived so there is only one secret to save.
    /// </summary>
    public static string PublicHostKeyFor(string hostKey)
    {
        if (!IsKey(hostKey))
        {
            throw new ArgumentException("A host key is 32 hex digits.", nameof(hostKey));
        }

        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("public:" + hostKey.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, KeyLength / 2).ToLowerInvariant();
    }

    public static bool IsKey(string? text)
    {
        if (text is null || text.Length != KeyLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Timings of the launcher's side of the relay. The defaults are for real use; tests shorten them.</summary>
public sealed class RelayClientOptions
{
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>How long to wait for the relay's answer to a handshake line. Longer than the relay waits for a host.</summary>
    public TimeSpan AnswerTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Silence on the control connection for this long means it is dead.</summary>
    public TimeSpan PongTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public TimeSpan MinBackoff { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>The line-based handshake, as the launcher speaks it. The relay's side is in src/STlauncher.Relay.</summary>
internal static class RelayWire
{
    private const int MaxLineLength = 256;

    public static async Task<TcpClient> DialAsync(RelayEndpoint relay, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);

            await client.ConnectAsync(relay.Host, relay.Port, limit.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one line a byte at a time. Slow on paper, but a handshake line is a few
    /// dozen bytes, and reading ahead would swallow the first bytes of the tunnel that
    /// follow the line on the same connection. Null when the line never came.
    /// </summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new byte[MaxLineLength];
        var one = new byte[1];
        var length = 0;

        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return null;
            }

            if (one[0] == '\n')
            {
                break;
            }

            if (one[0] == '\r')
            {
                continue;
            }

            if (length == line.Length)
            {
                return null;
            }

            line[length++] = one[0];
        }

        return Encoding.ASCII.GetString(line, 0, length);
    }

    /// <summary>Sends one line and reads the answer, both within <paramref name="timeout"/>.</summary>
    public static async Task<string?> AskAsync(Stream stream, string line, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        await WriteLineAsync(stream, line, limit.Token).ConfigureAwait(false);
        return await ReadLineAsync(stream, limit.Token).ConfigureAwait(false);
    }

    /// <summary>What an answer other than OK means. No answer at all counts as the relay being unreachable.</summary>
    public static RelayFailure FailureOf(string? answer) => answer switch
    {
        null => RelayFailure.RelayUnreachable,
        "ERR NOROOM" or "ERR NOHOST" or "ERR TIMEOUT" or "ERR NOCONN" => RelayFailure.HostOffline,
        "ERR LIMIT" or "ERR BUSY" or "ERR FULL" => RelayFailure.RelayBusy,
        _ => RelayFailure.Rejected
    };
}

/// <summary>Copies bytes both ways between two connections until either side is done.</summary>
internal static class StreamSplice
{
    private const int BufferSize = 16 * 1024;

    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(2);

    public static async Task RunAsync(NetworkStream first, NetworkStream second, CancellationToken cancellationToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var there = CopyAsync(first, second, cancel.Token);
        var back = CopyAsync(second, first, cancel.Token);

        await Task.WhenAny(there, back).ConfigureAwait(false);

        // One side has finished. The other gets a moment to deliver what it already
        // read, and then the pair is over.
        cancel.CancelAfter(Linger);
        await Task.WhenAll(there, back).ConfigureAwait(false);
    }

    private static async Task CopyAsync(NetworkStream from, NetworkStream to, CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            while (true)
            {
                var read = await from.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            to.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception)
        {
            // Reset, closed or cancelled: the tunnel is over whichever it was.
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
