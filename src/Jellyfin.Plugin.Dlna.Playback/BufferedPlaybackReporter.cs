using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Dlna.Playback;

/// <summary>
/// Keeps one latest desired state and one reporting worker per device. Slow backend
/// callbacks never run on a media read or block another device. Intermediate progress
/// is coalesced; transitions for the session actually reported remain serialized.
/// </summary>
// Mailbox is private and its synchronization object never escapes this reporter.
#pragma warning disable MT1000 // Lock objects are inaccessible outside the containing private mailbox.
public sealed class BufferedPlaybackReporter : IPlaybackReporter
{
    private readonly ConcurrentDictionary<(Guid User, string Device), Mailbox> _mailboxes = new();
    private readonly IPlaybackReporter _backend;
    private readonly Action<Exception> _error;

    /// <summary>Initializes a new instance of the <see cref="BufferedPlaybackReporter"/> class.</summary>
    /// <param name="backend">The reporting backend.</param>
    /// <param name="error">Reports asynchronous failures.</param>
    public BufferedPlaybackReporter(IPlaybackReporter backend, Action<Exception> error)
    {
        _backend = backend;
        _error = error;
    }

    /// <inheritdoc />
    public Task StartAsync(PlaybackIdentity identity, long positionTicks) => Enqueue(identity, positionTicks, false, true);

    /// <inheritdoc />
    public Task ProgressAsync(PlaybackIdentity identity, long positionTicks) => Enqueue(identity, positionTicks, false);

    /// <inheritdoc />
    public Task StopAsync(PlaybackIdentity identity, long positionTicks) => Enqueue(identity, positionTicks, true);

    /// <summary>Retry failed pending reports without waiting for backend callbacks.</summary>
    public void RetryPending()
    {
        foreach (var entry in _mailboxes)
        {
            lock (entry.Value.Sync)
            {
                Schedule(entry.Key, entry.Value);
            }
        }
    }

    /// <summary>Wait for current reporting workers; cancellation does not start competing callbacks.</summary>
    /// <param name="cancellationToken">Limits the wait during shutdown or tests.</param>
    /// <returns>A task completing once all currently scheduled work finishes.</returns>
    public Task DrainAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _mailboxes.Values.Select(mailbox =>
        {
            lock (mailbox.Sync)
            {
                return mailbox.Worker ?? Task.CompletedTask;
            }
        }).ToArray();
        return Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }

    private Task Enqueue(PlaybackIdentity identity, long ticks, bool stop, bool startOnly = false)
    {
        var key = (identity.UserId, identity.DeviceId);
        while (true)
        {
            var mailbox = _mailboxes.GetOrAdd(key, _ => new Mailbox());
            lock (mailbox.Sync)
            {
                if (mailbox.Retired)
                {
                    continue;
                }

                mailbox.Pending = new Report(identity, ticks, stop, startOnly);
                Schedule(key, mailbox);
                return Task.CompletedTask;
            }
        }
    }

    private void Schedule((Guid User, string Device) key, Mailbox mailbox)
    {
        if (!mailbox.Running && !mailbox.Retired && mailbox.Pending is not null)
        {
            mailbox.Running = true;
            mailbox.Worker = Task.Run(() => RunAsync(key, mailbox));
        }
    }

    private async Task RunAsync((Guid User, string Device) key, Mailbox mailbox)
    {
        while (true)
        {
            Report report;
            lock (mailbox.Sync)
            {
                if (mailbox.Pending is null)
                {
                    mailbox.Running = false;
                    if (mailbox.Current is null)
                    {
                        mailbox.Retired = true;
                        _mailboxes.TryRemove(key, out _);
                    }

                    return;
                }

                report = mailbox.Pending;
                mailbox.Pending = null;
            }

            try
            {
                var same = mailbox.Current?.ItemId == report.Identity.ItemId
                    && mailbox.Current?.MediaSourceId == report.Identity.MediaSourceId;
                if (mailbox.Current is not null && (!same || report.Stop))
                {
                    await _backend.StopAsync(mailbox.Current, same ? report.Ticks : mailbox.LastTicks).ConfigureAwait(false);
                    mailbox.Current = null;
                }

                if (!report.Stop)
                {
                    if (mailbox.Current is null)
                    {
                        await _backend.StartAsync(report.Identity, report.Ticks).ConfigureAwait(false);
                        mailbox.Current = report.Identity;
                    }

                    if (!report.StartOnly)
                    {
                        await _backend.ProgressAsync(report.Identity, report.Ticks).ConfigureAwait(false);
                    }

                    mailbox.LastTicks = report.Ticks;
                }
            }
            catch (Exception ex)
            {
                lock (mailbox.Sync)
                {
                    // A newer desired state wins over a failed older snapshot.
                    mailbox.Pending ??= report;
                    mailbox.Running = false;
                }

                _error(ex);
                return;
            }
        }
    }

    private sealed record Report(PlaybackIdentity Identity, long Ticks, bool Stop, bool StartOnly);

    private sealed class Mailbox
    {
        internal object Sync { get; } = new();

        public Report? Pending { get; set; }

        public PlaybackIdentity? Current { get; set; }

        public long LastTicks { get; set; }

        public bool Running { get; set; }

        public bool Retired { get; set; }

        public Task? Worker { get; set; }
    }
}

#pragma warning restore MT1000
