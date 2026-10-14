using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using STlauncher.Core.Modpacks;

namespace STlauncher.Core.Friends;

/// <summary>Why a build could not be left with the relay or taken from it, for the page to put into words.</summary>
public enum BuildShareFailure
{
    None,

    /// <summary>The relay did not answer: it is down, or the network between is.</summary>
    Unreachable,

    /// <summary>The relay is there but keeps no builds: a version from before sharing, or one run without a place to keep them.</summary>
    Unsupported,

    /// <summary>No build under this code: it was never given out, has a typo, or nobody asked for it in half a year.</summary>
    NotFound,

    /// <summary>Too many requests from this address for now.</summary>
    RateLimited,

    /// <summary>The build's code is longer than the relay keeps.</summary>
    TooBig,

    /// <summary>The relay has no room: too many connections, or its store is full.</summary>
    Busy,

    /// <summary>The relay refused for a reason this launcher does not know, or answered nonsense.</summary>
    Rejected
}

/// <summary>
/// A build under a short name. The long code ("STB1." and kilobytes of text) is left
/// with the relay, which keeps it on disk and gives back ten characters; a friend's
/// launcher asks for the text by those. The long code stays the real thing - it needs no
/// server and older launchers read it - and the short one is only a name for it.
///
/// What leaves the machine is exactly the long code: the build's name, the game version
/// and loader, and for each file its place in the build, its download address, hash and
/// size; files nobody can download go by name. No path outside the build, no nickname.
/// </summary>
public static class BuildShare
{
    /// <summary>What the relay keeps by default. A longer code is not even sent.</summary>
    public const int MaxLength = 256 * 1024;

    /// <summary>The longest answer worth reading: a relay may be set to keep more than the default.</summary>
    private const int MaxLines = 24_000;

    private const int Chunk = 200;

    // The alphabet of the invite codes: no I, L, O or U, so nothing is mistaken for a digit.
    private const string CodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int CodeLength = 10;

    /// <summary>
    /// "SB", then two groups of five. Dashes may be missing, doubled or typed as a long
    /// dash; a messenger that turned them into spaces is forgiven too.
    /// </summary>
    private static readonly Regex ShortCode = new(
        @"(?<![A-Za-z0-9])[Ss][Bb](?<a>[\s\-‐-―_]{0,3})([0-9A-Za-z]{5})(?<b>[\s\-‐-―_]{0,3})([0-9A-Za-z]{5})(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant);

    /// <summary>Not the relay's own patience but the player's: a share is a button press, not a download.</summary>
    private static RelayClientOptions Quick() => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AnswerTimeout = TimeSpan.FromSeconds(8)
    };

    /// <summary>The code as it is shown and sent: SB-XXXXX-XXXXX.</summary>
    public static string Format(string code) => "SB-" + code[..5] + "-" + code[5..];

    /// <summary>Whether the text holds a short build code.</summary>
    public static bool LooksLikeShortCode(string? text) => TryFindShortCode(text, out _);

    /// <summary>
    /// Finds a short build code in whatever was pasted - a whole chat message will do.
    /// Case does not matter, and letters people mix up are read as what was meant: O as
    /// zero, I and L as one.
    /// </summary>
    /// <param name="code">The ten characters, upper case, without the prefix and dashes.</param>
    public static bool TryFindShortCode(string? text, out string code)
    {
        code = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        for (var match = ShortCode.Match(text); match.Success; match = match.NextMatch())
        {
            var raw = (match.Groups[1].Value + match.Groups[2].Value).ToUpperInvariant().Replace('O', '0').Replace('I', '1').Replace('L', '1');

            if (!IsCode(raw))
            {
                continue;
            }

            // "sb hello world" is three words, not a code. Split by spaces alone, a code
            // has to look like one: no code of ours is all letters in lower case.
            var spaced = HasSpace(match.Groups["a"].Value) || HasSpace(match.Groups["b"].Value);

            if (spaced && !HasDigitOrUpper(match.Groups[1].Value + match.Groups[2].Value))
            {
                continue;
            }

            code = raw;
            return true;
        }

        return false;
    }

    /// <summary>Leaves a build's long code with the relay.</summary>
    /// <returns>The ten characters of the short code (see <see cref="Format"/>), or null with the reason.</returns>
    public static async Task<(string? Code, BuildShareFailure Failure)> PublishAsync(
        RelayEndpoint relay,
        string longCode,
        CancellationToken cancellationToken = default,
        RelayClientOptions? options = null)
    {
        if (string.IsNullOrEmpty(longCode) || !longCode.StartsWith(BuildCode.Prefix, StringComparison.Ordinal))
        {
            return (null, BuildShareFailure.Rejected);
        }

        if (longCode.Length > MaxLength)
        {
            return (null, BuildShareFailure.TooBig);
        }

        options ??= Quick();

        try
        {
            using var client = await RelayWire.DialAsync(relay, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();

            // The relay answers the first line before anything else is sent: one from
            // before sharing says ERR BADCMD and hangs up, and text written after that
            // would only reset the connection.
            var begin = await RelayWire.AskAsync(stream, "SHARE BEGIN", options.AnswerTimeout, cancellationToken).ConfigureAwait(false);

            if (begin != "OK")
            {
                return (null, FailureOf(begin));
            }

            var body = new StringBuilder(longCode.Length + (longCode.Length / Chunk * 9) + 32);

            for (var at = 0; at < longCode.Length; at += Chunk)
            {
                body.Append("SHARE + ").Append(longCode, at, Math.Min(Chunk, longCode.Length - at)).Append('\n');
            }

            body.Append("SHARE END\n");

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(options.AnswerTimeout);

            await stream.WriteAsync(Encoding.ASCII.GetBytes(body.ToString()), limit.Token).ConfigureAwait(false);
            await stream.FlushAsync(limit.Token).ConfigureAwait(false);

            var answer = await RelayWire.ReadLineAsync(stream, limit.Token).ConfigureAwait(false);

            if (answer is not null && answer.StartsWith("SHARED ", StringComparison.Ordinal))
            {
                var code = answer[7..].Trim().ToUpperInvariant();
                return IsCode(code) ? (code, BuildShareFailure.None) : (null, BuildShareFailure.Rejected);
            }

            return (null, FailureOf(answer));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, BuildShareFailure.Unreachable);
        }
    }

    /// <summary>Asks the relay for the long code kept under a short one.</summary>
    /// <param name="shortCode">The code in any form <see cref="TryFindShortCode"/> reads, or its bare ten characters.</param>
    public static async Task<(string? LongCode, BuildShareFailure Failure)> FetchAsync(
        RelayEndpoint relay,
        string shortCode,
        CancellationToken cancellationToken = default,
        RelayClientOptions? options = null)
    {
        var code = (shortCode ?? string.Empty).Trim().ToUpperInvariant();

        if (!IsCode(code) && !TryFindShortCode(shortCode, out code))
        {
            return (null, BuildShareFailure.NotFound);
        }

        options ??= Quick();

        try
        {
            using var client = await RelayWire.DialAsync(relay, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();

            var answer = await RelayWire.AskAsync(stream, "GETSHARE " + code, options.AnswerTimeout, cancellationToken).ConfigureAwait(false);

            if (answer is null || !answer.StartsWith("OK ", StringComparison.Ordinal) ||
                !int.TryParse(answer.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var lines) ||
                lines is < 1 or > MaxLines)
            {
                return (null, answer is not null && answer.StartsWith("OK", StringComparison.Ordinal) ? BuildShareFailure.Rejected : FailureOf(answer));
            }

            var text = new StringBuilder(lines * Chunk);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(options.AnswerTimeout);

            // Nothing but lines follows on this connection, so they can be read a
            // buffer at a time instead of the handshake's byte at a time.
            using var buffered = new BufferedStream(stream, 16 * 1024);

            for (var i = 0; i < lines; i++)
            {
                if (await RelayWire.ReadLineAsync(buffered, limit.Token).ConfigureAwait(false) is not { } line)
                {
                    return (null, BuildShareFailure.Unreachable);
                }

                text.Append(line);
            }

            var longCode = text.ToString();

            // The relay checked this when the build was left; a relay that is not ours
            // could answer anything.
            return longCode.StartsWith(BuildCode.Prefix, StringComparison.Ordinal)
                ? (longCode, BuildShareFailure.None)
                : (null, BuildShareFailure.Rejected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, BuildShareFailure.Unreachable);
        }
    }

    /// <summary>What an answer that is not the hoped-for one means. No answer at all counts as the relay being unreachable.</summary>
    private static BuildShareFailure FailureOf(string? answer) => answer switch
    {
        null => BuildShareFailure.Unreachable,

        // BADCMD is how a relay from before sharing answers a line it has never heard
        // of; NOSHARE is a new one that was given nowhere to keep builds.
        "ERR BADCMD" or "ERR NOSHARE" => BuildShareFailure.Unsupported,
        "ERR NOTFOUND" => BuildShareFailure.NotFound,
        "ERR LIMIT" => BuildShareFailure.RateLimited,
        "ERR TOOBIG" => BuildShareFailure.TooBig,
        "ERR BUSY" or "ERR FULL" => BuildShareFailure.Busy,
        _ => BuildShareFailure.Rejected
    };

    private static bool IsCode(string text)
    {
        if (text.Length != CodeLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (CodeAlphabet.IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSpace(string text)
    {
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDigitOrUpper(string text)
    {
        foreach (var c in text)
        {
            if (char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>What a pasted text turned out to hold.</summary>
public enum PastedCodeKind
{
    None,

    /// <summary>A build's long code ("STB1.").</summary>
    Build,

    /// <summary>A long invite to a friend's server ("STS1.").</summary>
    Invite,

    /// <summary>The short name of a shared build ("SB-XXXXX-XXXXX"); the relay holds the build.</summary>
    ShortBuild,

    /// <summary>The short name of an invite ("ST-XXXXX-XXXXX"); the relay holds the invite while its room is open.</summary>
    ShortInvite
}

/// <summary>
/// Tells the four codes a player may paste apart, so that every place a code can be
/// pasted into knows what it was given - and can say "this one goes over there" instead
/// of "this is not a code".
/// </summary>
public static class PastedCode
{
    /// <param name="shortCode">For the two short kinds, the ten characters without prefix and dashes.</param>
    public static PastedCodeKind Classify(string? text, out string shortCode)
    {
        shortCode = string.Empty;

        // A long code is the thing itself and wins over a short one that happens to
        // stand in the same message.
        switch (SharedCode.Detect(text))
        {
            case SharedCodeKind.Build:
                return PastedCodeKind.Build;

            case SharedCodeKind.Server:
                return PastedCodeKind.Invite;
        }

        if (RelayKeys.TryFindInviteCode(text, out shortCode))
        {
            return PastedCodeKind.ShortInvite;
        }

        return BuildShare.TryFindShortCode(text, out shortCode) ? PastedCodeKind.ShortBuild : PastedCodeKind.None;
    }
}
