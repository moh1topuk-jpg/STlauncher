using System;

namespace STlauncher.Core.Diagnostics;

public enum FreezeSignal
{
    /// <summary>Nothing to write.</summary>
    None,

    /// <summary>The silence has just reached the threshold: write the report.</summary>
    Started,

    /// <summary>Still silent, a while after the last write: refresh the duration.</summary>
    Ongoing,

    /// <summary>The interface answered after a reported freeze: write the full duration.</summary>
    Recovered
}

/// <param name="Signal">What the caller should do with the report.</param>
/// <param name="Silence">How long the interface has not answered, sleep excluded.</param>
/// <param name="Since">Wall-clock time the silence began; meaningful unless the signal is None.</param>
/// <param name="SendPing">True when the last ping was answered and a new one is due.</param>
public readonly record struct FreezeObservation(FreezeSignal Signal, TimeSpan Silence, DateTimeOffset Since, bool SendPing);

/// <summary>
/// Decides when the interface counts as frozen. Fed once per tick with both clocks and
/// whether the last ping came back; it holds no thread, no timer and no clock of its own,
/// which is what makes the thresholds and the sleep rule testable.
/// </summary>
/// <remarks>
/// Sleep is not a freeze. A closed lid or a suspended virtual machine shows up as one tick
/// that took far longer than a tick, on the monotonic clock, the wall clock or both
/// (which of the two keeps running through a suspend differs between systems). Such a gap
/// adds nothing to the silence, and silence gathered before it is dropped unless a freeze
/// was already reported: the first seconds after waking are slow for reasons that are not
/// the launcher's.
/// </remarks>
public sealed class FreezeDetector
{
    private readonly TimeSpan _threshold;
    private readonly TimeSpan _sleepGap;
    private readonly TimeSpan _refreshEvery;

    private bool _started;
    private TimeSpan _lastMonotonic;
    private DateTimeOffset _lastWall;

    private TimeSpan _silence;
    private DateTimeOffset _since;
    private bool _reported;
    private TimeSpan _silenceAtLastWrite;

    /// <param name="tick">How often <see cref="Observe"/> is expected to be called.</param>
    /// <param name="threshold">Silence that makes a freeze.</param>
    /// <param name="sleepGap">A tick longer than this was sleep. Defaults to four ticks.</param>
    /// <param name="refreshEvery">How often a freeze that goes on is written again. Defaults to twice the threshold.</param>
    public FreezeDetector(TimeSpan tick, TimeSpan threshold, TimeSpan? sleepGap = null, TimeSpan? refreshEvery = null)
    {
        if (tick <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tick));
        }

        if (threshold < tick)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        _threshold = threshold;
        _sleepGap = sleepGap ?? tick * 4;
        _refreshEvery = refreshEvery ?? threshold * 2;
    }

    /// <summary>True between a Started and its Recovered.</summary>
    public bool IsFrozen => _reported;

    /// <param name="monotonic">A clock that never goes back (a stopwatch).</param>
    /// <param name="wall">The system clock.</param>
    /// <param name="answered">The interface has answered the last ping (or none was sent yet).</param>
    /// <param name="answeredAt">Monotonic time of that answer, when known: makes the recovery time exact.</param>
    public FreezeObservation Observe(TimeSpan monotonic, DateTimeOffset wall, bool answered, TimeSpan? answeredAt = null)
    {
        if (!_started)
        {
            _started = true;
            _lastMonotonic = monotonic;
            _lastWall = wall;
            return new FreezeObservation(FreezeSignal.None, TimeSpan.Zero, wall, SendPing: true);
        }

        var step = monotonic - _lastMonotonic;
        var wallStep = wall - _lastWall;
        var previousMonotonic = _lastMonotonic;

        _lastMonotonic = monotonic;
        _lastWall = wall;

        // A wall clock set back by hand is not sleep; only a jump forward is.
        var slept = step > _sleepGap || wallStep > _sleepGap;

        if (step < TimeSpan.Zero)
        {
            step = TimeSpan.Zero;
        }

        if (answered)
        {
            if (!_reported)
            {
                _silence = TimeSpan.Zero;
                return new FreezeObservation(FreezeSignal.None, TimeSpan.Zero, wall, SendPing: true);
            }

            // The answer came somewhere inside this tick; count only the part before it.
            var tail = TimeSpan.Zero;

            if (!slept)
            {
                tail = answeredAt is { } at ? at - previousMonotonic : step;
                tail = tail < TimeSpan.Zero ? TimeSpan.Zero : tail > step ? step : tail;
            }

            var total = _silence + tail;
            var since = _since;

            _silence = TimeSpan.Zero;
            _reported = false;

            return new FreezeObservation(FreezeSignal.Recovered, total, since, SendPing: true);
        }

        if (slept)
        {
            if (!_reported)
            {
                _silence = TimeSpan.Zero;
            }

            return new FreezeObservation(FreezeSignal.None, _silence, _since, SendPing: false);
        }

        if (_silence == TimeSpan.Zero)
        {
            // The ping went out at the previous tick.
            _since = wall - step;
        }

        _silence += step;

        if (!_reported && _silence >= _threshold)
        {
            _reported = true;
            _silenceAtLastWrite = _silence;
            return new FreezeObservation(FreezeSignal.Started, _silence, _since, SendPing: false);
        }

        if (_reported && _silence - _silenceAtLastWrite >= _refreshEvery)
        {
            _silenceAtLastWrite = _silence;
            return new FreezeObservation(FreezeSignal.Ongoing, _silence, _since, SendPing: false);
        }

        return new FreezeObservation(FreezeSignal.None, _silence, _since, SendPing: false);
    }
}
