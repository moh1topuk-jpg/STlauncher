using System.Threading;

namespace STlauncher.Core.Diagnostics;

/// <summary>
/// "Only the latest request counts": every <see cref="Next"/> cancels the request before it
/// and hands out a token for the new one.
/// </summary>
/// <remarks>
/// The hand-written version of this kept a <see cref="CancellationTokenSource"/> in a field,
/// and each new request cancelled and disposed the previous source. That is a race twice
/// over: a delayed task that asks a disposed source for its token gets an
/// <see cref="System.ObjectDisposedException"/>, and two requests arriving on different
/// threads both read the same "previous" source, so the second cancels one the first has
/// already disposed. Either way the exception surfaces in a task nobody awaits.
/// Here the token is taken before the source is published, the swap is atomic, and the
/// superseded source is never disposed: a source without a timer or a linked parent holds
/// nothing but memory, and the collector takes it once the last waiter lets go.
/// </remarks>
public sealed class SupersedingCancellation
{
    private CancellationTokenSource? _current;

    /// <summary>Cancels the request in flight, if any, and returns the token of a new one.</summary>
    public CancellationToken Next()
    {
        var next = new CancellationTokenSource();
        var token = next.Token;

        Interlocked.Exchange(ref _current, next)?.Cancel();
        return token;
    }

    /// <summary>Cancels the request in flight without starting another.</summary>
    public void Cancel() => Interlocked.Exchange(ref _current, null)?.Cancel();
}
