using STlauncher.Core.Diagnostics;

namespace STlauncher.Core.Tests;

public class FreezeDetectorTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(15);
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(3));

    /// <summary>Both clocks, moved by hand.</summary>
    private sealed class Clocks
    {
        public TimeSpan Monotonic;
        public DateTimeOffset Wall = Start;

        public void Advance(TimeSpan by)
        {
            Monotonic += by;
            Wall += by;
        }
    }

    private static FreezeObservation Step(FreezeDetector detector, Clocks clocks, bool answered, TimeSpan? by = null)
    {
        clocks.Advance(by ?? Tick);
        return detector.Observe(clocks.Monotonic, clocks.Wall, answered);
    }

    [Fact]
    public void AnAnsweringInterfaceIsNeverReported()
    {
        var detector = new FreezeDetector(Tick, Threshold);
        var clocks = new Clocks();

        Assert.True(detector.Observe(clocks.Monotonic, clocks.Wall, answered: true).SendPing);

        for (var i = 0; i < 100; i++)
        {
            var observation = Step(detector, clocks, answered: true);
            Assert.Equal(FreezeSignal.None, observation.Signal);
            Assert.True(observation.SendPing);
        }
    }

    [Fact]
    public void SilenceBelowTheThresholdIsNotAFreeze()
    {
        var detector = new FreezeDetector(Tick, Threshold);
        var clocks = new Clocks();
        detector.Observe(clocks.Monotonic, clocks.Wall, answered: true);

        // Seven ticks: fourteen seconds of silence.
        for (var i = 0; i < 7; i++)
        {
            var observation = Step(detector, clocks, answered: false);
            Assert.Equal(FreezeSignal.None, observation.Signal);
            Assert.False(observation.SendPing);
        }

        Assert.Equal(FreezeSignal.None, Step(detector, clocks, answered: true).Signal);
        Assert.False(detector.IsFrozen);
    }

    [Fact]
    public void FifteenSecondsOfSilenceStartOneReportAndRecoveryCarriesTheFullDuration()
    {
        var detector = new FreezeDetector(Tick, Threshold);
        var clocks = new Clocks();
        detector.Observe(clocks.Monotonic, clocks.Wall, answered: true);
        var pinged = clocks.Wall;

        var signals = new List<FreezeSignal>();
        FreezeObservation last = default;

        // Twenty seconds of silence.
        for (var i = 0; i < 10; i++)
        {
            last = Step(detector, clocks, answered: false);
            signals.Add(last.Signal);
        }

        // Reported once, at the eighth tick (16 s is the first tick at or past 15 s).
        Assert.Equal(1, signals.Count(s => s == FreezeSignal.Started));
        Assert.Equal(FreezeSignal.Started, signals[7]);
        Assert.True(detector.IsFrozen);

        // The answer came one second into the next tick.
        clocks.Advance(Tick);
        var recovered = detector.Observe(clocks.Monotonic, clocks.Wall, answered: true, clocks.Monotonic - TimeSpan.FromSeconds(1));

        Assert.Equal(FreezeSignal.Recovered, recovered.Signal);
        Assert.Equal(TimeSpan.FromSeconds(21), recovered.Silence);
        Assert.Equal(pinged, recovered.Since);
        Assert.Equal(last.Since, recovered.Since);
        Assert.True(recovered.SendPing);
        Assert.False(detector.IsFrozen);
    }

    [Fact]
    public void ALongFreezeIsRefreshedButStaysOneFreeze()
    {
        var detector = new FreezeDetector(Tick, Threshold, refreshEvery: TimeSpan.FromSeconds(30));
        var clocks = new Clocks();
        detector.Observe(clocks.Monotonic, clocks.Wall, answered: true);

        var started = 0;
        var ongoing = 0;
        var since = new HashSet<DateTimeOffset>();

        for (var i = 0; i < 60; i++)
        {
            var observation = Step(detector, clocks, answered: false);

            if (observation.Signal == FreezeSignal.Started)
            {
                started++;
                since.Add(observation.Since);
            }
            else if (observation.Signal == FreezeSignal.Ongoing)
            {
                ongoing++;
                since.Add(observation.Since);
            }
        }

        Assert.Equal(1, started);
        Assert.Equal(3, ongoing);

        // The report's name comes from this moment, so it must not move while the freeze lasts.
        Assert.Single(since);
    }

    [Fact]
    public void AClosedLidIsNotSilence()
    {
        var detector = new FreezeDetector(Tick, Threshold);
        var clocks = new Clocks();
        detector.Observe(clocks.Monotonic, clocks.Wall, answered: true);

        // The ping goes out, the lid closes for an hour, the watchdog wakes before the interface.
        Assert.Equal(FreezeSignal.None, Step(detector, clocks, answered: false, TimeSpan.FromHours(1)).Signal);

        // A few slow seconds after waking are still far from a freeze.
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(FreezeSignal.None, Step(detector, clocks, answered: false).Signal);
        }

        Assert.Equal(FreezeSignal.None, Step(detector, clocks, answered: true).Signal);
    }

    [Fact]
    public void SilenceBeforeSleepIsForgotten()
    {
        var detector = new FreezeDetector(Tick, Threshold);
        var clocks = new Clocks();
        detector.Observe(clocks.Monotonic, clocks.Wall, answered: true);

        // Twelve seconds of silence, then sleep.
        for (var i = 0; i < 6; i++)
        {
            Step(detector, clocks, answered: false);
        }

        Step(detector, clocks, answered: false, TimeSpan.FromMinutes(20));

        // Twelve more: 24 s in all without the rule, but only 12 s count.
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(FreezeSignal.None, Step(detector, clocks, answered: false).Signal);
        }

        // And it still fires once the silence after waking is itself long enough.
        Assert.Equal(FreezeSignal.None, Step(detector, clocks, answered: false).Signal);
        Assert.Equal(FreezeSignal.Started, Step(detector, clocks, answered: false).Signal);
    }

    [Fact]
    public void ASuspendedMachineWhoseMonotonicClockStoodStillIsStillSleep()
    {
        var detector = new FreezeDetector(Tick, Threshold);
        var clocks = new Clocks();
        detector.Observe(clocks.Monotonic, clocks.Wall, answered: true);

        for (var i = 0; i < 6; i++)
        {
            Step(detector, clocks, answered: false);
        }

        // A paused virtual machine: the stopwatch saw one tick, the wall clock saw the night.
        clocks.Monotonic += Tick;
        clocks.Wall += TimeSpan.FromHours(8);
        Assert.Equal(FreezeSignal.None, detector.Observe(clocks.Monotonic, clocks.Wall, answered: false).Signal);

        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(FreezeSignal.None, Step(detector, clocks, answered: false).Signal);
        }
    }

    [Fact]
    public void SleepDuringAReportedFreezeAddsNothingToItsDuration()
    {
        var detector = new FreezeDetector(Tick, Threshold);
        var clocks = new Clocks();
        detector.Observe(clocks.Monotonic, clocks.Wall, answered: true);

        // Sixteen seconds: reported.
        for (var i = 0; i < 8; i++)
        {
            Step(detector, clocks, answered: false);
        }

        Assert.True(detector.IsFrozen);

        Step(detector, clocks, answered: false, TimeSpan.FromHours(2));
        Step(detector, clocks, answered: false);

        var recovered = Step(detector, clocks, answered: true);

        Assert.Equal(FreezeSignal.Recovered, recovered.Signal);
        Assert.Equal(TimeSpan.FromSeconds(20), recovered.Silence);
    }

    [Fact]
    public void AWallClockSetBackIsNotSleep()
    {
        var detector = new FreezeDetector(Tick, Threshold);
        var clocks = new Clocks();
        detector.Observe(clocks.Monotonic, clocks.Wall, answered: true);

        for (var i = 0; i < 4; i++)
        {
            Step(detector, clocks, answered: false);
        }

        clocks.Monotonic += Tick;
        clocks.Wall -= TimeSpan.FromHours(1);
        detector.Observe(clocks.Monotonic, clocks.Wall, answered: false);

        // 10 s so far; three more ticks reach 16 s.
        Step(detector, clocks, answered: false);
        Step(detector, clocks, answered: false);
        Assert.Equal(FreezeSignal.Started, Step(detector, clocks, answered: false).Signal);
    }
}

public class FreezeReportStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-freeze-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception)
        {
        }
    }

    private static FreezeReport Report(DateTimeOffset since, double seconds, bool recovered)
        => new(since, TimeSpan.FromSeconds(seconds), recovered, "0.5.7", "Windows 11", new[] { "section = Builds (for 40 s)" });

    [Fact]
    public void TheSameFreezeIsRewrittenNotAdded()
    {
        var store = FreezeReportStore.In(_root);
        var since = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(3));

        var first = store.Write(Report(since, 16, recovered: false));
        var second = store.Write(Report(since, 47, recovered: true));

        Assert.Equal(first, second);
        Assert.Single(store.List());

        var text = File.ReadAllText(second);
        Assert.Contains("STlauncher 0.5.7", text);
        Assert.Contains("duration = 47 s", text);
        Assert.Contains("answered again", text);
        Assert.Contains("section = Builds", text);
        Assert.DoesNotContain("at least", text);
    }

    [Fact]
    public void AFreezeThatNeverEndedSaysSo()
    {
        var text = Report(DateTimeOffset.Now, 16, recovered: false).Format();

        Assert.Contains("at least 16 s", text);
        Assert.Contains("still not answering", text);
    }

    [Fact]
    public void OnlyTheLastFewAreKept()
    {
        var store = new FreezeReportStore(Path.Combine(_root, "freezes"), keep: 3);
        var since = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(3));

        for (var i = 0; i < 6; i++)
        {
            store.Write(Report(since.AddMinutes(i), 20, recovered: true));
        }

        var kept = store.List();

        Assert.Equal(3, kept.Count);
        Assert.EndsWith("freeze-20261010-120500.txt", kept[0]);
        Assert.EndsWith("freeze-20261010-120300.txt", kept[2]);
    }

    [Fact]
    public void TheCountLooksOnlyAtRecentFreezes()
    {
        var store = FreezeReportStore.In(_root);
        var now = DateTimeOffset.Now;

        store.Write(Report(now.AddDays(-20), 20, recovered: true));
        store.Write(Report(now.AddDays(-2), 20, recovered: true));
        store.Write(Report(now.AddHours(-1), 20, recovered: true));

        Assert.Equal(2, store.CountSince(now.AddDays(-7)));
        Assert.Equal(0, FreezeReportStore.In(Path.Combine(_root, "nothing-here")).CountSince(now.AddDays(-7)));
    }
}

public class ActivitySnapshotTests
{
    [Fact]
    public void SaysWhatWasGoingOnAndForHowLong()
    {
        var snapshot = new ActivitySnapshot();
        var t0 = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(new[] { "section = unknown", "busy flags = none" }, snapshot.Describe(t0));

        snapshot.SetSection("Builds", t0);
        snapshot.SetFlag("IsInstalling", true, t0.AddSeconds(5));
        snapshot.SetFlag("IsBusy", true, t0.AddSeconds(10));

        // Raised again while already up: the moment it began stays.
        snapshot.SetFlag("IsInstalling", true, t0.AddSeconds(30));

        Assert.Equal(
            new[] { "section = Builds (for 1 min 35 s)", "IsBusy = true (for 1 min 25 s)", "IsInstalling = true (for 1 min 30 s)" },
            snapshot.Describe(t0.AddSeconds(95)));

        snapshot.SetFlag("IsInstalling", false, t0.AddSeconds(96));
        snapshot.SetFlag("IsBusy", false, t0.AddSeconds(96));

        Assert.Contains("busy flags = none", snapshot.Describe(t0.AddSeconds(97)));
    }
}

public class StartupGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stl-start-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>One start of the launcher: a fresh guard over the same file, as a new process would have.</summary>
    private (StartupGuard Guard, PreviousStart Previous) NewStart(string version = "0.5.7")
    {
        var guard = StartupGuard.In(_root);
        return (guard, guard.ReadPrevious(version));
    }

    [Fact]
    public void AFirstStartHasNothingHeldAgainstIt()
    {
        var (_, previous) = NewStart();

        Assert.False(previous.Failed);
        Assert.False(previous.AlreadyRepaired);
    }

    [Fact]
    public void AStartThatGotReadyIsNotAFailure()
    {
        var (guard, _) = NewStart();
        guard.MarkStarting();
        guard.MarkReady();

        Assert.False(NewStart().Previous.Failed);
    }

    [Fact]
    public void AStartKilledBeforeItGotReadyIsAFailure()
    {
        // The process dies: nothing after MarkStarting is ever written.
        NewStart().Guard.MarkStarting();

        Assert.True(NewStart().Previous.Failed);
    }

    [Fact]
    public void AStartThatStalledIsAFailureEvenIfThePlayerThenClosedIt()
    {
        var (guard, _) = NewStart();
        guard.MarkStarting();
        guard.MarkStalled();
        guard.MarkClosed();

        Assert.Equal(StartupState.Stalled, guard.State);
        Assert.True(NewStart().Previous.Failed);
    }

    [Fact]
    public void ASlowStartThatArrivedIsNotAFailure()
    {
        var (guard, _) = NewStart();
        guard.MarkStarting();
        guard.MarkStalled();
        guard.MarkReady();

        Assert.False(NewStart().Previous.Failed);
    }

    [Fact]
    public void AReadyStartCannotStallAfterwards()
    {
        var (guard, _) = NewStart();
        guard.MarkStarting();
        guard.MarkReady();

        // The timer firing late, or the launcher closing, changes nothing.
        guard.MarkStalled();
        guard.MarkClosed();

        Assert.Equal(StartupState.Ready, guard.State);
        Assert.False(NewStart().Previous.Failed);
    }

    [Fact]
    public void ClosingInOrderBeforeReadyIsThePlayersChoice()
    {
        var (guard, _) = NewStart();
        guard.MarkStarting();
        guard.MarkClosed();

        Assert.False(NewStart().Previous.Failed);
    }

    [Fact]
    public void AFailureOfAnotherVersionIsNotHeldAgainstThisOne()
    {
        NewStart("0.5.6").Guard.MarkStarting();

        Assert.False(NewStart("0.5.7").Previous.Failed);
    }

    [Fact]
    public void TheQuestionDoesNotFollowItself()
    {
        NewStart().Guard.MarkStarting();

        var (asking, previous) = NewStart();
        Assert.True(previous.Failed);

        // Killed with the question on screen.
        asking.MarkAsking();

        Assert.False(NewStart().Previous.Failed);
    }

    [Fact]
    public void ARepairIsRememberedForItsVersionOnly()
    {
        NewStart().Guard.MarkStarting();

        var (repairing, _) = NewStart();
        repairing.MarkAsking();
        repairing.MarkRepairing();

        // The restart after the repair: not a failure, and the repair is on record.
        var (afterRepair, previous) = NewStart();
        Assert.False(previous.Failed);
        Assert.True(previous.AlreadyRepaired);

        // It fails again: the next start knows the files were already put back once.
        afterRepair.MarkStarting();
        var (_, again) = NewStart();
        Assert.True(again.Failed);
        Assert.True(again.AlreadyRepaired);

        // The record of the repair survives ordinary starts...
        var (fine, _) = NewStart();
        fine.MarkStarting();
        fine.MarkReady();
        Assert.True(NewStart().Previous.AlreadyRepaired);

        // ...and does not carry over to the next version.
        Assert.False(NewStart("0.5.8").Previous.AlreadyRepaired);
    }

    [Fact]
    public void ADamagedRecordIsNoRecord()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, StartupGuard.FileName), "{ \"version\": \"0.5.7\", \"state\": ");

        var (guard, previous) = NewStart();
        Assert.False(previous.Failed);

        // And the guard writes over it without complaint.
        guard.MarkStarting();
        Assert.True(NewStart().Previous.Failed);
    }

    [Fact]
    public void AnUnknownVersionIsNeverAFailure()
    {
        NewStart(string.Empty).Guard.MarkStarting();

        Assert.False(NewStart(string.Empty).Previous.Failed);
    }
}

public class SupersedingCancellationTests
{
    [Fact]
    public void ANewRequestCancelsTheOneBeforeIt()
    {
        var latest = new SupersedingCancellation();

        var first = latest.Next();
        Assert.False(first.IsCancellationRequested);

        var second = latest.Next();
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);

        latest.Cancel();
        Assert.True(second.IsCancellationRequested);

        // Nothing in flight: cancelling again is not an error.
        latest.Cancel();
    }

    [Fact]
    public async Task ADelayedTaskSurvivesBeingSuperseded()
    {
        var latest = new SupersedingCancellation();
        var token = latest.Next();

        var waiter = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return "finished";
            }
            catch (OperationCanceledException)
            {
                // What the old code could not promise: the token is still usable here.
                return token.IsCancellationRequested && token.WaitHandle is not null ? "cancelled" : "broken";
            }
        });

        latest.Next();

        Assert.Equal("cancelled", await waiter.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    /// <summary>
    /// The race from the report: requests from several threads at once, each with a delayed
    /// task still holding its token. The hand-written cancel-and-dispose threw
    /// ObjectDisposedException here, in tasks nobody awaited.
    /// </summary>
    [Fact]
    public async Task RequestsFromManyThreadsNeverThrowAndLeaveExactlyOneAlive()
    {
        var latest = new SupersedingCancellation();
        var tokens = new System.Collections.Concurrent.ConcurrentBag<CancellationToken>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        using var go = new ManualResetEventSlim(false);

        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            go.Wait();

            try
            {
                for (var i = 0; i < 2000; i++)
                {
                    var token = latest.Next();
                    tokens.Add(token);

                    // What a debounced reload does with it.
                    using var registration = token.Register(() => { });
                    GC.KeepAlive(token.WaitHandle);
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToList();

        threads.ForEach(t => t.Start());
        go.Set();
        threads.ForEach(t => t.Join());

        Assert.Empty(failures);
        Assert.Equal(16000, tokens.Count);
        Assert.Equal(1, tokens.Count(t => !t.IsCancellationRequested));

        // Every superseded token can still be waited on.
        foreach (var token in tokens.Where(t => t.IsCancellationRequested).Take(50))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(1000, token));
        }
    }
}
