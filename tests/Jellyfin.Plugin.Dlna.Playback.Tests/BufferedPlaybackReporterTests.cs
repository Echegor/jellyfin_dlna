using System.Collections.Concurrent;
using Jellyfin.Plugin.Dlna.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Dlna.Playback.Tests;

public class BufferedPlaybackReporterTests
{
    private static readonly PlaybackIdentity Movie = new(Guid.NewGuid(), "phone", Guid.NewGuid(), "source", null, TimeSpan.FromMinutes(90).Ticks, 100 * 1024 * 1024);

    [Fact]
    public async Task BlockedReporterDoesNotBlockReadsOrOtherDeviceAndKeepsLatestPosition()
    {
        var backend = new Backend();
        var buffered = new BufferedPlaybackReporter(backend, _ => { });
        var tracker = new PlaybackTracker(buffered, TimeProvider.System, (_, _) => { });
        await using var stream = new ProgressTrackingStream(new MemoryStream(new byte[10 * 1024 * 1024]), tracker, Movie, "read", NullLogger.Instance, CancellationToken.None);
        Assert.Equal(10 * 1024 * 1024, await stream.ReadAsync(new byte[10 * 1024 * 1024]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 10000; i++)
        {
            await buffered.ProgressAsync(Movie, i);
        }

        await buffered.ProgressAsync(Movie with { DeviceId = "tv" }, 123);
        await backend.OtherDevice.Task.WaitAsync(TimeSpan.FromSeconds(5));
        backend.Release.TrySetResult();
        await buffered.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(9999, backend.Events.Last(e => e.Device == "phone" && e.Kind == "progress").Ticks);
        Assert.True(backend.Events.Count(e => e.Device == "phone" && e.Kind == "progress") <= 2);
        Assert.Equal(1, backend.MaxConcurrentPhone);
    }

    [Fact]
    public async Task StopWaitsForEarlierCallbackAndFailureCanRetry()
    {
        var backend = new Backend();
        var errors = new ConcurrentQueue<Exception>();
        var buffered = new BufferedPlaybackReporter(backend, errors.Enqueue);
        await buffered.ProgressAsync(Movie, 10);
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await buffered.StopAsync(Movie, 20);
        Assert.DoesNotContain(backend.Events, e => e.Kind == "stop");
        backend.Release.SetException(new IOException("delayed report failed"));
        await buffered.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(errors);
        buffered.RetryPending();
        await buffered.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(backend.Events, e => e.Kind == "stop" && e.Ticks == 20);
    }

    private sealed class Backend : IPlaybackReporter
    {
        private int _phoneActive;
        public int MaxConcurrentPhone;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OtherDevice { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(string Kind, string Device, long Ticks)> Events { get; } = new();
        public Task StartAsync(PlaybackIdentity identity, long ticks)
        {
            Events.Enqueue(("start", identity.DeviceId, ticks));
            return Task.CompletedTask;
        }

        public async Task ProgressAsync(PlaybackIdentity identity, long ticks)
        {
            if (identity.DeviceId == "phone")
            {
                MaxConcurrentPhone = Math.Max(MaxConcurrentPhone, Interlocked.Increment(ref _phoneActive));
                try
                {
                    if (!Entered.Task.IsCompleted)
                    {
                        Entered.SetResult();
                        await Release.Task;
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _phoneActive);
                }
            }

            Events.Enqueue(("progress", identity.DeviceId, ticks));
            if (identity.DeviceId == "tv") OtherDevice.TrySetResult();
        }

        public Task StopAsync(PlaybackIdentity identity, long ticks)
        {
            Events.Enqueue(("stop", identity.DeviceId, ticks));
            return Task.CompletedTask;
        }
    }
}
