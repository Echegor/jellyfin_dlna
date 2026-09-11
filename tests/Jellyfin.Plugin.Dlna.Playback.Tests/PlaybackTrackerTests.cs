using Jellyfin.Plugin.Dlna.Playback;
using Xunit;

namespace Jellyfin.Plugin.Dlna.Playback.Tests;

public class PlaybackTrackerTests
{
    private const long Mb = 1024 * 1024;
    private static readonly PlaybackIdentity Movie = new(Guid.NewGuid(), "phone", Guid.NewGuid(), "source", null, TimeSpan.FromMinutes(90).Ticks, 4300392594);

    [Fact]
    public async Task CapturedTailProbeNeverStartsOrCompletesPlayback()
    {
        var (tracker, reporter, clock) = Create();
        var initial = tracker.Open(Movie, "initial");
        await tracker.ObserveAsync(initial, 0, 5439488, 5439488);
        var tail = tracker.Open(Movie, "tail");
        await tracker.ObserveAsync(tail, 4300336835, 4300392594, 55759);
        await tracker.CloseAsync(tail);
        await tracker.CloseAsync(initial);
        Assert.Empty(reporter.Events);
        clock.Advance(11);
        await tracker.SweepAsync();
        Assert.Empty(reporter.Events);
    }

    [Fact]
    public async Task OldDisposalCannotOverwriteQualifiedSeek()
    {
        var (tracker, reporter, clock) = Create();
        var initial = tracker.Open(Movie, "initial");
        await tracker.ObserveAsync(initial, 139, 10 * Mb, 10 * Mb - 139);
        clock.Advance(3);
        var seek = tracker.Open(Movie, "seek");
        await tracker.ObserveAsync(seek, 1398616445, 1398616445 + 10 * Mb, 10 * Mb);
        var position = reporter.Events.Last().Ticks;
        Assert.True(position > TimeSpan.FromMinutes(29).Ticks);
        var count = reporter.Events.Count;
        await tracker.ObserveAsync(initial, 139, 30 * Mb, 30 * Mb - 139);
        await tracker.CloseAsync(initial);
        Assert.Equal(count, reporter.Events.Count);
        Assert.Single(reporter.Events, e => e.Kind == "start");
        await tracker.CloseAsync(seek);
        clock.Advance(11);
        await tracker.SweepAsync();
        Assert.Equal("stop", reporter.Events.Last().Kind);
        Assert.Equal(position, reporter.Events.Last().Ticks);
    }

    [Fact]
    public async Task DownloadingAheadDoesNotAdvancePlaybackClock()
    {
        var (tracker, reporter, clock) = Create();
        var request = tracker.Open(Movie, "buffer");
        await tracker.ObserveAsync(request, 0, 100 * Mb, 100 * Mb);
        Assert.Equal(0, reporter.Events.Last().Ticks);
        clock.Advance(5);
        await tracker.ObserveAsync(request, 0, 200 * Mb, 200 * Mb);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, reporter.Events.Last().Ticks);
    }

    [Fact]
    public async Task AdjacentShortRangesAccumulateEvidenceAndKeepOneStart()
    {
        var (tracker, reporter, clock) = Create();
        for (var i = 0; i < 12; i++)
        {
            var request = tracker.Open(Movie, $"range-{i}");
            await tracker.ObserveAsync(request, i * Mb, (i + 1) * Mb, Mb);
            await tracker.CloseAsync(request);
            clock.Advance(0.1);
        }

        Assert.Single(reporter.Events, e => e.Kind == "start");
        Assert.DoesNotContain(reporter.Events, e => e.Kind == "stop");
    }

    [Fact]
    public async Task ReconnectWithinGraceKeepsSessionButEventuallyEnds()
    {
        var (tracker, reporter, clock) = Create();
        var first = tracker.Open(Movie, "first");
        await tracker.ObserveAsync(first, 0, 10 * Mb, 10 * Mb);
        await tracker.CloseAsync(first);
        clock.Advance(9);
        await tracker.SweepAsync();
        Assert.DoesNotContain(reporter.Events, e => e.Kind == "stop");
        var next = tracker.Open(Movie, "next");
        await tracker.ObserveAsync(next, 10 * Mb, 20 * Mb, 10 * Mb);
        Assert.Single(reporter.Events, e => e.Kind == "start");
        Assert.Equal(0, reporter.Events.Last().Ticks);
        await tracker.CloseAsync(next);
        clock.Advance(11);
        await tracker.SweepAsync();
        Assert.Single(reporter.Events, e => e.Kind == "stop");
        await tracker.SweepAsync();
        Assert.Single(reporter.Events, e => e.Kind == "stop");
    }

    [Fact]
    public async Task InactiveOpenStreamExpires()
    {
        var (tracker, reporter, clock) = Create();
        await tracker.ObserveAsync(tracker.Open(Movie, "idle"), 0, 10 * Mb, 10 * Mb);
        clock.Advance(61);
        await tracker.SweepAsync();
        Assert.Equal("stop", reporter.Events.Last().Kind);
    }

    [Fact]
    public async Task DevicesAndUsersAreIndependent()
    {
        var (tracker, reporter, _) = Create();
        await tracker.ObserveAsync(tracker.Open(Movie, "phone"), 0, 10 * Mb, 10 * Mb);
        await tracker.ObserveAsync(tracker.Open(Movie with { DeviceId = "tv" }, "tv"), 0, 10 * Mb, 10 * Mb);
        await tracker.ObserveAsync(tracker.Open(Movie with { UserId = Guid.NewGuid() }, "other-user"), 0, 10 * Mb, 10 * Mb);
        Assert.Equal(3, reporter.Events.Count(e => e.Kind == "start"));
        Assert.DoesNotContain(reporter.Events, e => e.Kind == "stop");
    }

    [Fact]
    public async Task NewItemEndsPreviousItemAndOldReadsStayIgnored()
    {
        var (tracker, reporter, _) = Create();
        var old = tracker.Open(Movie, "old");
        await tracker.ObserveAsync(old, 0, 10 * Mb, 10 * Mb);
        await tracker.ObserveAsync(tracker.Open(Movie with { ItemId = Guid.NewGuid() }, "new"), 0, 10 * Mb, 10 * Mb);
        var count = reporter.Events.Count;
        await tracker.ObserveAsync(old, 0, 20 * Mb, 20 * Mb);
        await tracker.CloseAsync(old);
        Assert.Equal(count, reporter.Events.Count);
        Assert.Equal(new[] { "start", "progress", "stop", "start", "progress" }, reporter.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task DelayedCallbackSerializesLaterSeekAndFailureDoesNotDeadlock()
    {
        var (tracker, reporter, _) = Create();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reporter.BlockProgress = blocked.Task;
        var old = tracker.Open(Movie, "old");
        var first = tracker.ObserveAsync(old, 0, 10 * Mb, 10 * Mb);
        var second = tracker.ObserveAsync(tracker.Open(Movie, "seek"), 1000 * Mb, 1010 * Mb, 10 * Mb);
        Assert.False(second.IsCompleted);
        reporter.BlockProgress = null;
        blocked.SetException(new IOException("simulated report failure"));
        await Assert.ThrowsAsync<IOException>(() => first);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reporter.Events.Last().Ticks > TimeSpan.FromMinutes(20).Ticks);
        await tracker.CloseAsync(old);
    }

    [Fact]
    public async Task BackwardSeekIsAllowedButStaleForwardPositionIsNot()
    {
        var (tracker, reporter, clock) = Create();
        var old = tracker.Open(Movie, "forward");
        await tracker.ObserveAsync(old, 1000 * Mb, 1010 * Mb, 10 * Mb);
        clock.Advance(5);
        await tracker.ObserveAsync(tracker.Open(Movie, "back"), 100 * Mb, 110 * Mb, 10 * Mb);
        var back = reporter.Events.Last().Ticks;
        await tracker.ObserveAsync(old, 1000 * Mb, 1020 * Mb, 20 * Mb);
        Assert.Equal(back, reporter.Events.Last().Ticks);
        Assert.True(back < TimeSpan.FromMinutes(3).Ticks);
    }

    [Fact]
    public async Task EmptyOrUnknownLengthRequestsNeverStartPlayback()
    {
        var (tracker, reporter, _) = Create();
        await tracker.CloseAsync(tracker.Open(Movie, "empty"));
        await tracker.ObserveAsync(tracker.Open(Movie with { Length = 0 }, "unknown"), 0, 10 * Mb, 10 * Mb);
        Assert.Empty(reporter.Events);
    }

    [Fact]
    public async Task ExpiredRequestCannotResurrectSession()
    {
        var (tracker, reporter, clock) = Create();
        var old = tracker.Open(Movie, "old");
        await tracker.ObserveAsync(old, 0, 10 * Mb, 10 * Mb);
        clock.Advance(61);
        await tracker.SweepAsync();
        var count = reporter.Events.Count;
        await tracker.ObserveAsync(old, 0, 20 * Mb, 20 * Mb);
        Assert.Equal(count, reporter.Events.Count);
        await tracker.ObserveAsync(tracker.Open(Movie, "fresh"), 0, 10 * Mb, 10 * Mb);
        Assert.Equal(2, reporter.Events.Count(e => e.Kind == "start"));
    }

    [Fact]
    public async Task SmallTailProbeDoesNotSupersedeAnActiveStream()
    {
        var (tracker, reporter, clock) = Create();
        var active = tracker.Open(Movie, "active");
        await tracker.ObserveAsync(active, 0, 10 * Mb, 10 * Mb);
        var probe = tracker.Open(Movie, "probe");
        await tracker.ObserveAsync(probe, Movie.Length - 55759, Movie.Length, 55759);
        await tracker.CloseAsync(probe);
        clock.Advance(5);
        await tracker.ObserveAsync(active, 0, 20 * Mb, 20 * Mb);
        Assert.Single(reporter.Events, e => e.Kind == "start");
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, reporter.Events.Last().Ticks);
    }

    [Fact]
    public async Task MaintenanceFailureDoesNotPreventOtherDevicesFromEndingAndRetries()
    {
        var (tracker, reporter, clock) = Create();
        await tracker.ObserveAsync(tracker.Open(Movie, "phone"), 0, 10 * Mb, 10 * Mb);
        await tracker.ObserveAsync(tracker.Open(Movie with { DeviceId = "tv" }, "tv"), 0, 10 * Mb, 10 * Mb);
        reporter.FailStopDevice = "phone";
        clock.Advance(61);
        await Assert.ThrowsAsync<AggregateException>(() => tracker.SweepAsync());
        Assert.Single(reporter.Events, e => e.Kind == "stop" && e.Identity.DeviceId == "tv");
        reporter.FailStopDevice = null;
        await tracker.SweepAsync();
        Assert.Equal(2, reporter.Events.Count(e => e.Kind == "stop"));
    }

    [Fact]
    public async Task ShutdownEndsSessionOnceWithoutWaitingForGrace()
    {
        var (tracker, reporter, _) = Create();
        await tracker.ObserveAsync(tracker.Open(Movie, "active"), 0, 10 * Mb, 10 * Mb);
        await tracker.SweepAsync(shutdown: true);
        await tracker.SweepAsync(shutdown: true);
        Assert.Single(reporter.Events, e => e.Kind == "stop");
    }

    [Fact]
    public async Task BufferedPlaybackGetsPeriodicLiveUpdatesWithoutAdditionalReads()
    {
        var (tracker, reporter, clock) = Create();
        await tracker.ObserveAsync(tracker.Open(Movie, "buffer"), 0, 100 * Mb, 100 * Mb);
        clock.Advance(5);
        await tracker.SweepAsync();
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, reporter.Events.Last().Ticks);
    }

    [Fact]
    public async Task EmptyBufferDoesNotAccumulatePlaybackWhileWaitingForMoreBytes()
    {
        var (tracker, reporter, clock) = Create();
        var request = tracker.Open(Movie, "slow");
        await tracker.ObserveAsync(request, 0, 10 * Mb, 10 * Mb);
        clock.Advance(30);
        await tracker.ObserveAsync(request, 0, 100 * Mb, 100 * Mb);
        var tenMbTicks = (long)((double)(10 * Mb) / Movie.Length * Movie.DurationTicks);
        Assert.Equal(tenMbTicks, reporter.Events.Last().Ticks);
    }

    private static (PlaybackTracker Tracker, Reporter Reporter, Clock Clock) Create()
    {
        var reporter = new Reporter();
        var clock = new Clock();
        return (new PlaybackTracker(reporter, clock, (_, _) => { }), reporter, clock);
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(double seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private sealed class Reporter : IPlaybackReporter
    {
        public List<(string Kind, PlaybackIdentity Identity, long Ticks)> Events { get; } = new();
        public Task? BlockProgress { get; set; }
        public string? FailStopDevice { get; set; }
        public Task StartAsync(PlaybackIdentity identity, long ticks)
        {
            Events.Add(("start", identity, ticks));
            return Task.CompletedTask;
        }

        public async Task ProgressAsync(PlaybackIdentity identity, long ticks)
        {
            if (BlockProgress is { } blocked)
            {
                await blocked;
            }

            Events.Add(("progress", identity, ticks));
        }

        public Task StopAsync(PlaybackIdentity identity, long ticks)
        {
            if (identity.DeviceId == FailStopDevice)
            {
                throw new IOException("stop failure");
            }

            Events.Add(("stop", identity, ticks));
            return Task.CompletedTask;
        }
    }
}
