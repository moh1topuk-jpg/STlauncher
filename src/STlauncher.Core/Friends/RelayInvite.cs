using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace STlauncher.Core.Friends;

/// <summary>
/// The guest's half of a short invite: asks the relay for the full invite a host left
/// under a code. The host has to be online, because the relay keeps nothing on disk: an
/// invite lives exactly as long as its room.
/// </summary>
public static class RelayInvite
{
    private const int MaxLines = 400;

    /// <returns>The full invite text, or null with the reason: the relay is not there, or no room answers to the code.</returns>
    public static async Task<(string? Invite, RelayFailure Failure)> FetchAsync(
        RelayEndpoint relay,
        string code,
        RelayClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RelayClientOptions();

        try
        {
            using var client = await RelayWire.DialAsync(relay, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();

            var answer = await RelayWire.AskAsync(stream, "FETCH " + code, options.AnswerTimeout, cancellationToken).ConfigureAwait(false);

            if (answer is null || !answer.StartsWith("OK ", StringComparison.Ordinal) ||
                !int.TryParse(answer.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var lines) ||
                lines is < 1 or > MaxLines)
            {
                // A code nobody answers to and a host that has not left an invite yet are
                // the same thing to a guest: nothing to join right now.
                return (null, answer is "ERR NOINVITE" ? RelayFailure.HostOffline : RelayWire.FailureOf(answer));
            }

            var text = new StringBuilder();

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(options.AnswerTimeout);

            for (var i = 0; i < lines; i++)
            {
                if (await RelayWire.ReadLineAsync(stream, limit.Token).ConfigureAwait(false) is not { } line)
                {
                    return (null, RelayFailure.RelayUnreachable);
                }

                text.Append(line);
            }

            return (text.ToString(), RelayFailure.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, RelayFailure.RelayUnreachable);
        }
    }
}
