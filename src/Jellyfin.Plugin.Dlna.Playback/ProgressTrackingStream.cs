using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dlna.Playback;

/// <summary>Observes file reads without equating an HTTP response with playback.</summary>
public sealed class ProgressTrackingStream : Stream
{
    private readonly Stream _inner;
    private readonly PlaybackTracker _tracker;
    private readonly PlaybackRequest _request;
    private readonly string _requestId;
    private readonly ILogger _logger;
    private readonly CancellationToken _aborted;
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private long? _firstByte;
    private long _transferred;
    private long _lastObservationBytes;
    private long _lastObservationMs;
    private long _endByte;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="ProgressTrackingStream"/> class.</summary>
    /// <param name="inner">A stable, seekable file stream.</param>
    /// <param name="tracker">Shared coordinator.</param>
    /// <param name="identity">Playback metadata.</param>
    /// <param name="requestId">HTTP request identifier.</param>
    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="aborted">HTTP cancellation status.</param>
    public ProgressTrackingStream(Stream inner, PlaybackTracker tracker, PlaybackIdentity identity, string requestId, ILogger logger, CancellationToken aborted)
    {
        _inner = inner;
        _tracker = tracker;
        _requestId = requestId;
        _request = tracker.Open(identity, requestId);
        _logger = logger;
        _aborted = aborted;
        Log("stream-open");
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set
        {
            _inner.Position = value;
            _logger.LogDebug("DLNA trace request={RequestId} position-set={Position}", _requestId, value);
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        // Stream's synchronous contract requires completing the observation here.
        ObserveAsync(read).GetAwaiter().GetResult();
        return read;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        await ObserveAsync(read).ConfigureAwait(false);
        return read;
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = _inner.Seek(offset, origin);
        _logger.LogDebug("DLNA trace request={RequestId} seek origin={Origin} offset={Offset} result={Position}", _requestId, origin, offset, position);
        return position;
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override void SetLength(long value) => _inner.SetLength(value);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                CloseAsync().GetAwaiter().GetResult();
            }
            finally
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private async Task ObserveAsync(int read)
    {
        if (read <= 0)
        {
            return;
        }

        _endByte = _inner.Position;
        _firstByte ??= _endByte - read;
        _transferred += read;
        if (_transferred == read)
        {
            Log("first-read");
        }

        // No per-buffer callback or unbounded fire-and-forget queue. The final
        // observation is flushed on disposal, including short bounded ranges.
        if (_transferred - _lastObservationBytes >= 1024 * 1024 || _lifetime.ElapsedMilliseconds - _lastObservationMs >= 1000)
        {
            await ReportAsync().ConfigureAwait(false);
        }
    }

    private async Task ReportAsync()
    {
        try
        {
            if (_firstByte.HasValue)
            {
                await _tracker.ObserveAsync(_request, _firstByte.Value, _endByte, _transferred).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DLNA trace request={RequestId} observation-failed; playback transfer continues", _requestId);
        }

        _lastObservationBytes = _transferred;
        _lastObservationMs = _lifetime.ElapsedMilliseconds;
    }

    private async Task CloseAsync()
    {
        Log("dispose");
        try
        {
            await ReportAsync().ConfigureAwait(false);
            await _tracker.CloseAsync(_request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DLNA trace request={RequestId} close-observation-failed", _requestId);
        }
    }

    private void Log(string phase)
    {
        _logger.LogDebug("DLNA trace request={RequestId} phase={Phase} elapsedMs={ElapsedMs} firstByte={FirstByte} endByte={EndByte} transferredBytes={TransferredBytes} aborted={Aborted}", _requestId, phase, _lifetime.ElapsedMilliseconds, _firstByte, _endByte, _transferred, _aborted.IsCancellationRequested);
    }
}
