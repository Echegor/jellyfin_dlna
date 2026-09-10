#pragma warning disable CS1591, SA1214, SA1028, CS1572, CS1573
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.Dlna.Playback;

public class ProgressTrackingStream : Stream
{
    private readonly Stream _innerStream;
    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly string _sessionId;
    private readonly BaseItem _item;
    private readonly User _user;
    private readonly long _totalLength;
    private readonly long _durationTicks;

    private long _bytesRead;
    private long _lastReportedTicks;
    private long _currentPositionTicks;

    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private int _disposed;

    public ProgressTrackingStream(
        Stream innerStream,
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        string sessionId,
        BaseItem item,
        User user,
        long totalLength)
    {
        _innerStream = innerStream;
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _sessionId = sessionId;
        _item = item;
        _user = user;
        _totalLength = totalLength;
        _durationTicks = item.RunTimeTicks ?? 0;

        if (_innerStream.CanSeek && _innerStream.Length > 0)
        {
            try
            {
                _lastReportedTicks = (long)((double)_innerStream.Position / _innerStream.Length * _durationTicks);
            }
            catch
            {
                _lastReportedTicks = 0;
            }
        }
        else
        {
            _lastReportedTicks = 0;
        }

        _currentPositionTicks = _lastReportedTicks;
    }

    public override bool CanRead => _innerStream.CanRead;

    public override bool CanSeek => _innerStream.CanSeek;

    public override bool CanWrite => _innerStream.CanWrite;

    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush() => _innerStream.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _innerStream.Read(buffer, offset, count);
        TrackProgress(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await _innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        TrackProgress(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        TrackProgress(read);
        return read;
    }

    private void TrackProgress(int bytesRead)
    {
        if (bytesRead <= 0 || _durationTicks <= 0)
        {
            return;
        }

        long currentPositionTicks = 0;
        bool fallback = false;

        if (CanSeek)
        {
            try
            {
                if (Length > 0)
                {
                    currentPositionTicks = (long)((double)Position / Length * _durationTicks);
                }
            }
            catch
            {
                fallback = true;
            }
        }
        else
        {
            fallback = true;
        }

        if (fallback && _totalLength > 0)
        {
            _bytesRead += bytesRead;
            currentPositionTicks = (long)((double)_bytesRead / _totalLength * _durationTicks);
        }
        else if (fallback)
        {
            _bytesRead += bytesRead;
        }

        if (currentPositionTicks > 0)
        {
            _currentPositionTicks = currentPositionTicks;
        }

        // Ignore progress updates if the stream has been open for less than 3 seconds (likely a metadata probe)
        if (_stopwatch.Elapsed.TotalSeconds < 3)
        {
            return;
        }

        if (currentPositionTicks <= 0)
        {
            return;
        }

        // Report progress every 10 seconds (10,000,000 ticks) or if sought backward
        if (Math.Abs(currentPositionTicks - _lastReportedTicks) > 10000000)
        {
            _lastReportedTicks = currentPositionTicks;
            _sessionManager.OnPlaybackProgress(new PlaybackProgressInfo
            {
                ItemId = _item.Id,
                PositionTicks = currentPositionTicks,
                SessionId = _sessionId,
                IsPaused = false
            });
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);

    public override void SetLength(long value) => _innerStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing && System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _innerStream.Dispose();
            }
            finally
            {
                _ = _sessionManager.OnPlaybackStopped(new PlaybackStopInfo
                {
                    ItemId = _item.Id,
                    SessionId = _sessionId,
                    PositionTicks = _currentPositionTicks
                });
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                await _innerStream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await _sessionManager.OnPlaybackStopped(new PlaybackStopInfo { ItemId = _item.Id, SessionId = _sessionId, PositionTicks = _currentPositionTicks }).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
