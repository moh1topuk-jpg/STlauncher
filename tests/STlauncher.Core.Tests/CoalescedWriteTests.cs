using STlauncher.Core.Storage;

namespace STlauncher.Core.Tests;

/// <summary>
/// Settings are written a moment after they are asked for, together with whatever else is
/// asked for meanwhile. What must hold: a burst is one write of the newest text, nothing
/// asked for is ever lost, and the same text is not written over itself.
/// </summary>
public class CoalescedWriteTests
{
    /// <summary>A timer the test fires by hand.</summary>
    private sealed class ManualTimer
    {
        private readonly List<(Action Action, Handle Handle)> _armed = new();

        public int Armed => _armed.Count(a => !a.Handle.Disposed);

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var handle = new Handle();
            _armed.Add((action, handle));
            return handle;
        }

        public void Fire()
        {
            foreach (var (action, handle) in _armed.ToList())
            {
                if (!handle.Disposed)
                {
                    action();
                }
            }

            _armed.Clear();
        }

        private sealed class Handle : IDisposable
        {
            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }
    }

    private static (CoalescedWrite Writer, List<string> Written, ManualTimer Timer) Create()
    {
        var written = new List<string>();
        var timer = new ManualTimer();
        return (new CoalescedWrite(written.Add, TimeSpan.FromSeconds(1), timer.Schedule), written, timer);
    }

    [Fact]
    public void A_burst_of_requests_is_one_write_of_the_last()
    {
        var (writer, written, timer) = Create();

        writer.Request("one");
        writer.Request("two");
        writer.Request("three");

        Assert.Empty(written);
        Assert.Equal(1, timer.Armed);

        timer.Fire();

        Assert.Equal(new[] { "three" }, written);
        Assert.False(writer.HasPending);
    }

    [Fact]
    public void Flush_writes_what_is_waiting_at_once_and_the_timer_then_has_nothing_to_do()
    {
        var (writer, written, timer) = Create();

        writer.Request("kept");
        writer.Flush();

        Assert.Equal(new[] { "kept" }, written);

        timer.Fire();

        Assert.Single(written);
    }

    [Fact]
    public void Flush_with_nothing_waiting_writes_nothing()
    {
        var (writer, written, _) = Create();

        writer.Flush();

        Assert.Empty(written);
    }

    [Fact]
    public void The_text_already_on_disk_is_not_written_again()
    {
        var (writer, written, timer) = Create();

        writer.Seed("same");
        writer.Request("same");

        Assert.False(writer.HasPending);
        Assert.Equal(0, timer.Armed);

        writer.Request("other");
        timer.Fire();
        writer.Request("other");
        timer.Fire();

        Assert.Equal(new[] { "other" }, written);
    }

    [Fact]
    public void A_change_and_back_within_one_burst_writes_nothing()
    {
        var (writer, written, timer) = Create();

        writer.Seed("as it was");
        writer.Request("changed");
        writer.Request("as it was");
        timer.Fire();

        Assert.Empty(written);
        Assert.False(writer.HasPending);
    }

    [Fact]
    public void A_request_after_a_write_starts_a_new_wait()
    {
        var (writer, written, timer) = Create();

        writer.Request("first");
        timer.Fire();
        writer.Request("second");

        Assert.Equal(1, timer.Armed);

        timer.Fire();

        Assert.Equal(new[] { "first", "second" }, written);
    }

    [Fact]
    public void A_failed_delayed_write_is_reported_and_still_owed()
    {
        var attempts = 0;
        var written = new List<string>();
        var timer = new ManualTimer();
        var writer = new CoalescedWrite(
            text =>
            {
                if (++attempts == 1)
                {
                    throw new IOException("disk full");
                }

                written.Add(text);
            },
            TimeSpan.FromSeconds(1),
            timer.Schedule);

        Exception? reported = null;
        writer.Failed += ex => reported = ex;

        writer.Request("precious");
        timer.Fire();

        Assert.IsType<IOException>(reported);
        Assert.True(writer.HasPending);

        // The exit flush is the second chance.
        writer.Flush();

        Assert.Equal(new[] { "precious" }, written);
    }

    [Fact]
    public void A_failed_flush_throws_and_keeps_the_text_for_the_next_try()
    {
        var fail = true;
        var written = new List<string>();
        var timer = new ManualTimer();
        var writer = new CoalescedWrite(
            text =>
            {
                if (fail)
                {
                    throw new IOException("locked");
                }

                written.Add(text);
            },
            TimeSpan.FromSeconds(1),
            timer.Schedule);

        writer.Request("precious");

        Assert.Throws<IOException>(() => writer.Flush());
        Assert.True(writer.HasPending);

        fail = false;
        writer.Flush();

        Assert.Equal(new[] { "precious" }, written);
    }

    [Fact]
    public void After_discard_nothing_more_is_written()
    {
        var (writer, written, timer) = Create();

        writer.Request("into the old folder");
        writer.Discard();
        timer.Fire();
        writer.Request("still the old folder");
        timer.Fire();
        writer.Flush();

        Assert.Empty(written);
    }

    [Fact]
    public void With_the_real_timer_a_burst_reaches_the_disk_once()
    {
        var written = new List<string>();
        using var done = new ManualResetEventSlim();
        using var writer = new CoalescedWrite(
            text =>
            {
                lock (written)
                {
                    written.Add(text);
                }

                done.Set();
            },
            TimeSpan.FromMilliseconds(40));

        for (var i = 0; i < 20; i++)
        {
            writer.Request("value " + i);
        }

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));

        lock (written)
        {
            Assert.Equal(new[] { "value 19" }, written);
        }
    }
}
