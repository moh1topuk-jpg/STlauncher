using System;
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

/// <summary>What a failed request actually failed at.</summary>
public sealed record NetworkFailure(NetworkFailureKind Kind, string Detail);

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

        var detail = InnermostMessage(exception);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case TaskCanceledException or TimeoutException:
                    return new NetworkFailure(NetworkFailureKind.Timeout, detail);

                case AuthenticationException:
                    return new NetworkFailure(NetworkFailureKind.Blocked, detail);

                case SocketException socket:
                    return new NetworkFailure(
                        socket.SocketErrorCode is SocketError.ConnectionReset
                            or SocketError.ConnectionRefused
                            or SocketError.ConnectionAborted
                            ? NetworkFailureKind.Blocked
                            : NetworkFailureKind.NoConnection,
                        detail);

                case HttpRequestException { StatusCode: not null }:
                    return new NetworkFailure(NetworkFailureKind.HttpError, detail);

                case IOException when current.InnerException is null:
                    // A stream that ended mid-handshake, with nothing underneath to name
                    // the cause. Reads the same way to the player as an active block.
                    return new NetworkFailure(NetworkFailureKind.Blocked, detail);
            }
        }

        return new NetworkFailure(NetworkFailureKind.Unknown, detail);
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
