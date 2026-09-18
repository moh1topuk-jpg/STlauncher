using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using STlauncher.Core.Http;
using Xunit;

namespace STlauncher.Core.Tests;

public class NetworkFailureTests
{
    /// <summary>
    /// The shape one of the first users hit: the outer message says only "see inner
    /// exception", and the cause is two levels down.
    /// </summary>
    private static Exception SslHandshakeCutShort() =>
        new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new AuthenticationException(
                "Authentication failed because the remote party closed the transport stream.",
                new SocketException((int)SocketError.ConnectionReset)));

    [Fact]
    public void Classify_NamesABlockedConnection()
    {
        var failure = NetworkFailures.Classify(SslHandshakeCutShort());

        Assert.Equal(NetworkFailureKind.Blocked, failure.Kind);
    }

    [Fact]
    public void InnermostMessage_ReachesThroughTheWrapper()
    {
        // The innermost message carries its own text rather than the OS wording, which is
        // localised - asserting on the socket message would only pass on English Windows.
        var cause = new IOException("the cause");

        var detail = NetworkFailures.InnermostMessage(
            new HttpRequestException(
                "The SSL connection could not be established, see inner exception.",
                new AuthenticationException("Authentication failed.", cause)));

        // Anything but "see inner exception", which is what the player was shown.
        Assert.Equal("the cause", detail);
    }

    [Fact]
    public void Classify_SeparatesNoRouteFromBeingCutOff()
    {
        var unreachable = new HttpRequestException(
            "No such host is known.",
            new SocketException((int)SocketError.HostNotFound));

        Assert.Equal(NetworkFailureKind.NoConnection, NetworkFailures.Classify(unreachable).Kind);
    }

    [Fact]
    public void Classify_KeepsHttpErrorsApart()
    {
        var notFound = new HttpRequestException("Not found", null, HttpStatusCode.NotFound);

        Assert.Equal(NetworkFailureKind.HttpError, NetworkFailures.Classify(notFound).Kind);
    }

    [Fact]
    public void Classify_RecognisesATimeout()
    {
        var timeout = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");

        Assert.Equal(NetworkFailureKind.Timeout, NetworkFailures.Classify(timeout).Kind);
    }

    [Fact]
    public void Classify_TreatsAStreamThatEndedAsABlock()
    {
        var truncated = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new IOException("The response ended prematurely."));

        Assert.Equal(NetworkFailureKind.Blocked, NetworkFailures.Classify(truncated).Kind);
    }

    [Fact]
    public void Classify_FallsBackRatherThanGuessing()
    {
        var odd = new InvalidOperationException("something else entirely");

        var failure = NetworkFailures.Classify(odd);

        Assert.Equal(NetworkFailureKind.Unknown, failure.Kind);
        Assert.Equal("something else entirely", failure.Detail);
    }
}
