using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace STlauncher.Core.Http;

public enum NetworkFailureKind
{
    Unknown,

    /// <summary>Nothing answered: no route, no DNS, nothing listening.</summary>
    NoConnection,

    /// <summary>
    /// The connection was cut during or just after the TLS handshake. In practice that is
    /// a provider or a security product interfering, not a fault in the launcher.
    /// </summary>
    Blocked,

    /// <summary>The host answered, and said no.</summary>
    HttpError,

    Timeout
}

/// <summary>
/// The one thing that went wrong, narrow enough to say in a sentence. The kind above
/// groups these for the places that only need "is it the network or the service".
/// </summary>
public enum NetworkFailureCause
{
    Unknown,

    /// <summary>The name of the host did not resolve to an address.</summary>
    NameNotResolved,

    /// <summary>The address answered that nothing listens there.</summary>
    ConnectionRefused,

    /// <summary>The connection was there and was cut.</summary>
    ConnectionReset,

    /// <summary>No route to the address at all.</summary>
    Unreachable,

    /// <summary>
    /// The certificate the other side showed is not one Windows trusts. With the real
    /// sites this is an antivirus or a proxy that opens HTTPS and signs it again with a
    /// certificate of its own that is not in the system store.
    /// </summary>
    CertificateNotTrusted,

    /// <summary>The secure connection failed for another reason than the certificate.</summary>
    SecureChannelFailed,

    TimedOut,

    /// <summary>The server answered with an error status.</summary>
    HttpStatus,

    /// <summary>The file came whole and is not the file that was promised.</summary>
    HashMismatch,

    /// <summary>The server sent the request on to an address the launcher does not follow.</summary>
    RedirectRefused
}

/// <summary>What a failed request actually failed at.</summary>
public sealed record NetworkFailure(
    NetworkFailureKind Kind,
    string Detail,
    NetworkFailureCause Cause = NetworkFailureCause.Unknown);

/// <summary>The bytes that came do not have the digest the source promised.</summary>
public sealed class HashMismatchException : IOException
{
    public HashMismatchException(string message)
        : base(message)
    {
    }
}

/// <summary>A redirect the launcher refuses to follow: not https, or one too many.</summary>
public sealed class RedirectRefusedException : HttpRequestException
{
    public RedirectRefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A file that could not be fetched. The message starts with the cause in plain words and
/// ends with the address: the status line shows the beginning of a long text, and the
/// address used to take all of it.
/// </summary>
public sealed class DownloadFailedException : IOException
{
    public DownloadFailedException(string url, NetworkFailure failure, Exception? inner)
        : base(NetworkFailures.Describe(failure) + ": " + url, inner)
    {
        Url = url;
        Failure = failure;
    }

    public string Url { get; }

    public NetworkFailure Failure { get; }
}

/// <summary>
/// Turns a network exception into something worth showing a player.
/// </summary>
/// <remarks>
/// The outer message of a failed HTTPS request is "The SSL connection could not be
/// established, see inner exception". That is what one of the first users was shown when
/// the update check failed: it names no cause, suggests no action, and hides the one
/// message that would have explained it. The cause always sits further down the chain.
/// </remarks>
public static class NetworkFailures
{
    public static NetworkFailure Classify(Exception exception)
    {
        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        // Classified once already, further down: the verdict travels with the exception.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DownloadFailedException known)
            {
                return known.Failure;
            }
        }

        var detail = InnermostMessage(exception);
        var cause = CauseOf(exception);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HashMismatchException:
                    return new NetworkFailure(NetworkFailureKind.Unknown, detail, cause);

                case RedirectRefusedException:
                    return new NetworkFailure(NetworkFailureKind.HttpError, detail, cause);

                case TaskCanceledException or TimeoutException:
                    return new NetworkFailure(NetworkFailureKind.Timeout, detail, cause);

                case AuthenticationException:
                    return new NetworkFailure(NetworkFailureKind.Blocked, detail, cause);

                case SocketException socket:
                    return new NetworkFailure(
                        socket.SocketErrorCode is SocketError.ConnectionReset
                            or SocketError.ConnectionRefused
                            or SocketError.ConnectionAborted
                            ? NetworkFailureKind.Blocked
                            : NetworkFailureKind.NoConnection,
                        detail,
                        cause);

                case HttpRequestException { StatusCode: not null }:
                    return new NetworkFailure(NetworkFailureKind.HttpError, detail, cause);

                case IOException when current.InnerException is null:
                    // A stream that ended mid-handshake, with nothing underneath to name
                    // the cause. Reads the same way to the player as an active block.
                    return new NetworkFailure(NetworkFailureKind.Blocked, detail, cause);
            }
        }

        return new NetworkFailure(NetworkFailureKind.Unknown, detail, cause);
    }

    /// <summary>
    /// The narrow cause, read from the whole chain. The types decide wherever they can;
    /// the wording of a message is asked only about the certificate, which the runtime
    /// reports in no other way.
    /// </summary>
    private static NetworkFailureCause CauseOf(Exception exception)
    {
        var fallback = NetworkFailureCause.Unknown;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HashMismatchException:
                    return NetworkFailureCause.HashMismatch;

                case RedirectRefusedException:
                    return NetworkFailureCause.RedirectRefused;

                case TaskCanceledException or TimeoutException:
                    return NetworkFailureCause.TimedOut;

                case AuthenticationException authentication:
                    return IsAboutTheCertificate(authentication)
                        ? NetworkFailureCause.CertificateNotTrusted
                        : SocketCause(authentication) ?? NetworkFailureCause.SecureChannelFailed;

                case SocketException socket:
                    return SocketCause(socket) ?? NetworkFailureCause.Unreachable;

                case HttpRequestException { StatusCode: not null }:
                    return NetworkFailureCause.HttpStatus;

                case HttpRequestException request when fallback == NetworkFailureCause.Unknown:
                    // What the runtime itself says about the request, for the chains that
                    // end without a socket error underneath.
                    fallback = request.HttpRequestError switch
                    {
                        HttpRequestError.NameResolutionError => NetworkFailureCause.NameNotResolved,
                        HttpRequestError.SecureConnectionError => NetworkFailureCause.SecureChannelFailed,
                        HttpRequestError.ResponseEnded => NetworkFailureCause.ConnectionReset,
                        _ => NetworkFailureCause.Unknown
                    };
                    break;

                case IOException when current.InnerException is null && fallback == NetworkFailureCause.Unknown:
                    fallback = NetworkFailureCause.ConnectionReset;
                    break;
            }
        }

        return fallback;
    }

    private static NetworkFailureCause? SocketCause(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData or SocketError.NoRecovery
                        => NetworkFailureCause.NameNotResolved,
                    SocketError.ConnectionRefused => NetworkFailureCause.ConnectionRefused,
                    SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown
                        => NetworkFailureCause.ConnectionReset,
                    SocketError.TimedOut => NetworkFailureCause.TimedOut,
                    _ => NetworkFailureCause.Unreachable
                };
            }
        }

        return null;
    }

    // SEC_E_UNTRUSTED_ROOT, SEC_E_CERT_EXPIRED, SEC_E_WRONG_PRINCIPAL, SEC_E_CERT_UNKNOWN,
    // CERT_E_UNTRUSTEDROOT, CERT_E_CHAINING: what Windows answers when it is the
    // certificate, on the paths where the runtime passes the system's code through.
    private static readonly int[] CertificateCodes =
    {
        unchecked((int)0x80090325), unchecked((int)0x80090328), unchecked((int)0x80090322),
        unchecked((int)0x80090327), unchecked((int)0x800B0109), unchecked((int)0x800B010A)
    };

    private static bool IsAboutTheCertificate(AuthenticationException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception native && Array.IndexOf(CertificateCodes, native.NativeErrorCode) >= 0)
            {
                return true;
            }
        }

        // "The remote certificate is invalid because of errors in the certificate chain:
        // UntrustedRoot". The runtime's own messages are not translated.
        return exception.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The cause as a short English sentence, for the log and for exception messages. The
    /// window has its own words for each cause in the player's language.
    /// </summary>
    public static string Describe(NetworkFailure failure)
    {
        if (failure is null)
        {
            throw new ArgumentNullException(nameof(failure));
        }

        return failure.Cause switch
        {
            NetworkFailureCause.NameNotResolved => "The address of the site was not found (DNS)",
            NetworkFailureCause.ConnectionRefused => "The connection was refused",
            NetworkFailureCause.ConnectionReset => "The connection was cut",
            NetworkFailureCause.Unreachable => "There is no route to the site",
            NetworkFailureCause.CertificateNotTrusted =>
                "The site's certificate is not trusted - an antivirus or a proxy is likely inspecting HTTPS",
            NetworkFailureCause.SecureChannelFailed => "A secure connection could not be set up",
            NetworkFailureCause.TimedOut => "The site did not answer in time",
            NetworkFailureCause.HttpStatus => "The site answered with an error (" + failure.Detail + ")",
            NetworkFailureCause.HashMismatch => "The file that came is not the one that was promised (hash mismatch)",
            NetworkFailureCause.RedirectRefused => "The site sent the request to an address that is not followed (" + failure.Detail + ")",
            _ => failure.Detail
        };
    }

    /// <summary>The message at the bottom of the chain, where the real cause lives.</summary>
    public static string InnermostMessage(Exception exception)
    {
        var current = exception ?? throw new ArgumentNullException(nameof(exception));

        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }
}
