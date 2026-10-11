using System;
using System.Threading;

namespace STlauncher.Core.Storage;

/// <summary>
/// Turns a burst of "save this" into one write. A setting changed by a tick box asks for
/// the whole settings file to be written; a dozen of them changing together used to write
/// it a dozen times, each one a flush to disk on the thread that draws the window.
/// </summary>
/// <remarks>
/// The newest text asked for is the one written, a fixed while after the first request of
/// the burst, off the caller's thread. Text equal to what is already on disk is not written
/// at all. <see cref="Flush"/> writes what is waiting at once, on the caller's thread: it
/// is what an exit, a hide to the tray and a game start call, so nothing asked for is lost.
/// </remarks>
public sealed class CoalescedWrite : IDisposable
{
    private readonly object _gate = new();

    /// <summary>Held for the length of a write, so two of them never interleave and the later text lands last.</summary>
    private readonly object _writing = new();

    private readonly Action<string> _write;
    private readonly TimeSpan _delay;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;

    private string? _pending;
    private string? _written;
    private IDisposable? _timer;
    private bool _discarding;

    /// <param name="write">Puts the text on disk. Called from the timer or from <see cref="Flush"/>, never twice at once.</param>
    /// <param name="delay">How long the first request of a burst waits for the rest of it.</param>
    /// <param name="schedule">Runs an action once after a delay; the default is a thread-pool timer. Tests pass their own.</param>
    public CoalescedWrite(Action<string> write, TimeSpan delay, Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _delay = delay;
        _schedule = schedule ?? ((due, action) => new Timer(_ => action(), null, due, Timeout.InfiniteTimeSpan));
    }

    /// <summary>A delayed write failed. Raised on the timer's thread; a failed <see cref="Flush"/> throws instead.</summary>
    public event Action<Exception>? Failed;

    /// <summary>True while a request has not reached the disk yet.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending is not null;
            }
        }
    }

    /// <summary>Tells the writer what the file holds now, so the same text is not written over itself.</summary>
    public void Seed(string? onDisk)
    {
        lock (_gate)
        {
            _written = onDisk;
        }
    }

    /// <summary>Asks for the text to be on disk soon. A later request replaces an earlier one still waiting.</summary>
    public void Request(string text)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        lock (_gate)
        {
            if (_discarding)
            {
                return;
            }

            if (_pending is null && string.Equals(text, _written, StringComparison.Ordinal))
            {
                return;
            }

            _pending = text;
            _timer ??= _schedule(_delay, OnTimer);
        }
    }

    /// <summary>Writes what is waiting, now and on this thread. Throws what the write throws.</summary>
    public void Flush() => WritePending(rethrow: true);

    /// <summary>
    /// Drops what is waiting and refuses anything further: the data has moved elsewhere,
    /// and a late write would recreate the file in the folder that was left.
    /// </summary>
    public void Discard()
    {
        lock (_gate)
        {
            _discarding = true;
            _pending = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnTimer() => WritePending(rethrow: false);

    private void WritePending(bool rethrow)
    {
        lock (_writing)
        {
            string? text;

            lock (_gate)
            {
                text = _pending;
                _pending = null;
                _timer?.Dispose();
                _timer = null;

                if (text is null || string.Equals(text, _written, StringComparison.Ordinal))
                {
                    return;
                }
            }

            try
            {
                _write(text);

                lock (_gate)
                {
                    _written = text;
                }
            }
            catch (Exception ex)
            {
                // Still owed: the next request or flush tries again with it, unless a newer text has come.
                lock (_gate)
                {
                    if (!_discarding)
                    {
                        _pending ??= text;
                    }
                }

                if (rethrow)
                {
                    throw;
                }

                Failed?.Invoke(ex);
            }
        }
    }
}
