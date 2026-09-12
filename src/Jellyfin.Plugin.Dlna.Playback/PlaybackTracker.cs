using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Dlna.Playback;

/// <summary>
/// Coordinates HTTP observations by user/device. A monotonic request number prevents
/// superseded streams from changing a newer playback position. All callbacks for a
/// device are awaited under the same gate, including failures and cleanup.
/// </summary>
public sealed class PlaybackTracker
{
    private static readonly TimeSpan EvidenceWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<(Guid User, string Device), DeviceState> _devices = new();
    private readonly IPlaybackReporter _reporter;
    private readonly TimeProvider _clock;
    private readonly Action<string, string> _diagnostic;
    private long _sequence;
    private int _shutdown;

    /// <summary>Initializes a new instance of the <see cref="PlaybackTracker"/> class.</summary>
    /// <param name="reporter">Ordered playback callbacks.</param>
    /// <param name="clock">Monotonic clock.</param>
    /// <param name="diagnostic">Request decision logging.</param>
    public PlaybackTracker(IPlaybackReporter reporter, TimeProvider clock, Action<string, string> diagnostic)
    {
        _reporter = reporter;
        _clock = clock;
        _diagnostic = diagnostic;
    }

    /// <summary>Create a request identity; registration alone does not start playback.</summary>
    /// <param name="identity">Playback identity and file metadata.</param>
    /// <param name="requestId">HTTP trace identifier.</param>
    /// <returns>A request observation handle.</returns>
    public PlaybackRequest Open(PlaybackIdentity identity, string requestId)
    {
        return new PlaybackRequest(identity, requestId, Interlocked.Increment(ref _sequence));
    }

    /// <summary>Observe bytes read. Callers throttle observations, never playback callbacks.</summary>
    /// <param name="request">Request handle.</param>
    /// <param name="firstByte">First byte offset read.</param>
    /// <param name="endByte">Exclusive last byte offset read.</param>
    /// <param name="bytesRead">Total bytes transferred by this request.</param>
    /// <returns>The observation task.</returns>
    public async Task ObserveAsync(PlaybackRequest request, long firstByte, long endByte, long bytesRead)
    {
        var key = (request.Identity.UserId, request.Identity.DeviceId);
        while (true)
        {
            if (Volatile.Read(ref _shutdown) != 0 || request.IsSuperseded?.Invoke() == true)
            {
                _diagnostic(request.Id, "ignored: superseded-or-shutdown");
                return;
            }

            var state = _devices.GetOrAdd(key, _ => new DeviceState());
            await state.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (state.Retired)
                {
                    continue;
                }

                if (request.Closed || request.Sequence < state.Owner)
                {
                    _diagnostic(request.Id, "ignored: superseded-or-closed");
                    return;
                }

                request.IsSuperseded = () => state.Owner > request.Sequence;
                var now = _clock.GetTimestamp();
                var identity = request.Identity;
                if (identity.Length <= 0 || identity.DurationTicks <= 0 || bytesRead <= 0)
                {
                    _diagnostic(request.Id, "ignored: unknown-length-or-duration");
                    return;
                }

                // EOF probes are common for container indexes. They never contribute to
                // initial qualification, even if other reads from the beginning qualify.
                var tailProbe = firstByte >= identity.Length - Math.Min(identity.Length / 100, 1024 * 1024)
                    && bytesRead < 1024 * 1024;
                if (tailProbe)
                {
                    _diagnostic(request.Id, "ignored: small-tail-probe");
                    return;
                }

                var sameEvidence = state.EvidenceItem == identity.ItemId
                    && state.EvidenceSource == identity.MediaSourceId
                    && _clock.GetElapsedTime(state.EvidenceAt, now) <= EvidenceWindow
                    && firstByte >= state.EvidenceFrom && firstByte <= state.EvidenceTo + (256 * 1024);
                if (!sameEvidence)
                {
                    state.EvidenceItem = identity.ItemId;
                    state.EvidenceSource = identity.MediaSourceId;
                    state.EvidenceFrom = firstByte;
                    state.EvidenceTo = firstByte;
                    state.EvidenceStarted = now;
                }

                state.EvidenceTo = Math.Max(state.EvidenceTo, endByte);
                state.EvidenceAt = now;
                var evidenceBytes = state.EvidenceTo - state.EvidenceFrom;
                var qualified = evidenceBytes >= 8 * 1024 * 1024
                    || (evidenceBytes >= 1024 * 1024 && _clock.GetElapsedTime(state.EvidenceStarted, now) >= TimeSpan.FromSeconds(2));
                if (!qualified && state.Owner != request.Sequence)
                {
                    _diagnostic(request.Id, "pending: insufficient-playback-evidence");
                    return;
                }

                var sameItem = state.Identity?.ItemId == identity.ItemId && state.Identity?.MediaSourceId == identity.MediaSourceId;
                if (state.Identity is not null && !sameItem)
                {
                    await _reporter.StopAsync(state.Identity, Position(state, now)).ConfigureAwait(false);
                    state.Identity = null;
                }

                var previousOwner = state.Owner;
                var starting = state.Identity is null;
                if (state.Identity is not null)
                {
                    // Do not accumulate elapsed playback while the inferred buffer
                    // is empty; a reconnect can credit the buffered gap.
                    state.AnchorTicks = Position(state, now);
                    state.AnchorAt = now;
                }

                if (state.Identity is null)
                {
                    var start = state.Owner == request.Sequence && state.ResumeTicks.HasValue
                        ? state.ResumeTicks.Value : ToTicks(firstByte, identity);
                    state.ResumeTicks = null;
                    await _reporter.StartAsync(identity, start).ConfigureAwait(false);
                    state.Identity = identity;
                    state.AnchorTicks = start;
                    state.AnchorAt = now;
                    state.LastReport = now;
                    state.To = endByte;
                }
                else if (previousOwner != request.Sequence)
                {
                    // A range inside already downloaded coverage is a continuation;
                    // one outside it is an estimated seek (forward or backward).
                    var currentByte = (long)((double)Position(state, now) / identity.DurationTicks * identity.Length);
                    if (firstByte < currentByte - (256 * 1024) || firstByte > state.To + (256 * 1024))
                    {
                        state.AnchorTicks = ToTicks(firstByte, identity);
                        state.AnchorAt = now;
                        state.To = endByte;
                    }
                }

                state.Owner = request.Sequence;
                state.OwnerClosed = false;
                state.LastRead = now;
                state.To = Math.Max(state.To, endByte);
                state.DownloadTicks = ToTicks(state.To, identity);
                if (starting || _clock.GetElapsedTime(state.LastReport, now) >= ReportInterval)
                {
                    await _reporter.ProgressAsync(identity, Position(state, now)).ConfigureAwait(false);
                    state.LastReport = now;
                }
            }
            finally
            {
                state.Gate.Release();
            }

            return;
        }
    }

    /// <summary>Close a request. Never report its own stale position during disposal.</summary>
    /// <param name="request">Request being closed.</param>
    /// <returns>The close observation task.</returns>
    public async Task CloseAsync(PlaybackRequest request)
    {
        request.Closed = true;
        if (!_devices.TryGetValue((request.Identity.UserId, request.Identity.DeviceId), out var state))
        {
            return;
        }

        await state.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (state.Owner == request.Sequence)
            {
                var now = _clock.GetTimestamp();
                state.AnchorTicks = Position(state, now);
                state.AnchorAt = now;
                state.OwnerClosed = true;
                state.ClosedAt = now;
                _diagnostic(request.Id, "owner-closed: reconnect-grace");
            }
            else
            {
                _diagnostic(request.Id, "closed: no-session-update");
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>Expire idle sessions and pending probes. Called by the hosted service.</summary>
    /// <param name="shutdown">Whether all sessions should end.</param>
    /// <returns>The maintenance task.</returns>
    public async Task SweepAsync(bool shutdown = false)
    {
        if (shutdown)
        {
            Interlocked.Exchange(ref _shutdown, 1);
        }

        List<Exception>? failures = null;
        foreach (var pair in _devices)
        {
            var state = pair.Value;
            await state.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = _clock.GetTimestamp();
                if (state.Retired)
                {
                    continue;
                }

                var expired = state.Identity is null
                    ? _clock.GetElapsedTime(state.EvidenceAt, now) >= EvidenceWindow
                    : state.OwnerClosed
                        ? _clock.GetElapsedTime(state.ClosedAt, now) >= ReconnectWindow
                        : _clock.GetElapsedTime(state.LastRead, now) >= IdleTimeout;
                if (shutdown || expired)
                {
                    if (state.Identity is not null)
                    {
                        var finalPosition = state.OwnerClosed ? state.AnchorTicks : Position(state, now);
                        await _reporter.StopAsync(state.Identity, finalPosition).ConfigureAwait(false);
                        state.ResumeTicks = finalPosition;
                        state.Identity = null;
                    }

                    // A timed-out but open owner can resume reading later. Keep its
                    // generation watermark so superseded requests stay rejected.
                    if (!shutdown && state.Owner != 0 && !state.OwnerClosed)
                    {
                        continue;
                    }

                    state.Retired = true;
                    _devices.TryRemove(pair.Key, out _);
                }
                else if (state.Identity is not null && !state.OwnerClosed && _clock.GetElapsedTime(state.LastReport, now) >= ReportInterval)
                {
                    await _reporter.ProgressAsync(state.Identity, Position(state, now)).ConfigureAwait(false);
                    state.LastReport = now;
                }
            }
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
            }
            finally
            {
                state.Gate.Release();
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("DLNA session maintenance failed; retry on next sweep.", failures);
        }
    }

    private static long ToTicks(long bytes, PlaybackIdentity identity) => (long)(Math.Clamp((double)bytes / identity.Length, 0, 1) * identity.DurationTicks);

    private long Position(DeviceState state, long now)
    {
        var elapsed = _clock.GetElapsedTime(state.AnchorAt, now).Ticks;
        if (state.OwnerClosed)
        {
            // Buffered playback can continue between short HTTP requests. Credit
            // the gap only within the reconnect window, bounded by downloaded data.
            elapsed = Math.Min(elapsed, ReconnectWindow.Ticks);
        }

        return Math.Min(state.DownloadTicks, state.AnchorTicks + elapsed);
    }

    private sealed class DeviceState
    {
        private bool _retired;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public PlaybackIdentity? Identity { get; set; }

        public bool Retired
        {
            get => Volatile.Read(ref _retired);
            set => Volatile.Write(ref _retired, value);
        }

        public long Owner { get; set; }

        public long? ResumeTicks { get; set; }

        public bool OwnerClosed { get; set; }

        public long ClosedAt { get; set; }

        public long LastRead { get; set; }

        public long LastReport { get; set; }

        public long AnchorAt { get; set; }

        public long AnchorTicks { get; set; }

        public long DownloadTicks { get; set; }

        public long To { get; set; }

        public Guid EvidenceItem { get; set; }

        public string? EvidenceSource { get; set; }

        public long EvidenceFrom { get; set; }

        public long EvidenceTo { get; set; }

        public long EvidenceStarted { get; set; }

        public long EvidenceAt { get; set; }
    }
}
