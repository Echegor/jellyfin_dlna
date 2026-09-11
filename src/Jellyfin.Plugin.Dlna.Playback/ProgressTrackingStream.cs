#pragma warning disable CS1591, SA1214, SA1028, CS1572, CS1573
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger _logger;

    private readonly string _requestId;
    private readonly CancellationToken _requestAborted;
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private long _transferredBytes;
    private long _reportSequence;
    private readonly bool _canSeekForDiagnostics;
    private long _bytesRead;
    private long _lastReportedTicks;
    private long _currentPositionTicks;

    private int _disposed;

    public ProgressTrackingStream(
        Stream innerStream,
        ISessionManager sessionManager,
        IUserDataManager userDataManager,
        string sessionId,
        BaseItem item,
        User user,
        long totalLength,
        ILogger logger,
        string requestId,
        CancellationToken requestAborted)
    {
        _innerStream = innerStream;
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _sessionId = sessionId;
        _item = item;
        _user = user;
        _totalLength = totalLength;
        _durationTicks = item.RunTimeTicks ?? 0;
        _logger = logger;
        _canSeekForDiagnostics = innerStream.CanSeek;
        _requestId = requestId;
        _requestAborted = requestAborted;

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
        LogState("stream-open");
    }

    public override bool CanRead => _innerStream.CanRead;

    public override bool CanSeek => _innerStream.CanSeek;

    public override bool CanWrite => _innerStream.CanWrite;

    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set
        {
            _innerStream.Position = value;
            _logger.LogDebug("DLNA trace request={RequestId} session={SessionId} position-set bytes={Position}", _requestId, _sessionId, value);
        }
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
        if (bytesRead > 0 && Interlocked.Add(ref _transferredBytes, bytesRead) == bytesRead)
        {
            LogState("first-read");
        }
        else if (bytesRead == 0)
        {
            LogState("end-of-stream");
        }

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
            Interlocked.Add(ref _bytesRead, bytesRead);
            currentPositionTicks = (long)((double)Interlocked.Read(ref _bytesRead) / _totalLength * _durationTicks);
        }
        else if (fallback)
        {
            Interlocked.Add(ref _bytesRead, bytesRead);
        }

        if (currentPositionTicks > 0)
        {
            Interlocked.Exchange(ref _currentPositionTicks, currentPositionTicks);
        }

        if (currentPositionTicks <= 0)
        {
            return;
        }

        // Report progress every 10 seconds (100,000,000 ticks) or if sought backward
        if (Math.Abs(currentPositionTicks - _lastReportedTicks) > 100000000)
        {
            _logger.LogDebug("DLNA ProgressTrackingStream reporting progress {Ticks} for {SessionId}", currentPositionTicks, _sessionId);
            _lastReportedTicks = currentPositionTicks;

            ReportProgress("read", currentPositionTicks);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = _innerStream.Seek(offset, origin);
        _logger.LogDebug("DLNA trace request={RequestId} session={SessionId} seek origin={Origin} offset={Offset} result={Position}", _requestId, _sessionId, origin, offset, position);
        return position;
    }

    private void LogState(string phase)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var session = _sessionManager.Sessions.FirstOrDefault(candidate => candidate.Id == _sessionId);
        _logger.LogDebug(
            "DLNA trace request={RequestId} session={SessionId} item={ItemId} phase={Phase} elapsedMs={ElapsedMs} transferredBytes={TransferredBytes} estimatedTicks={EstimatedTicks} durationTicks={DurationTicks} sourceBytes={SourceBytes} seekable={Seekable} aborted={Aborted} nowPlaying={NowPlaying} sessionTicks={SessionTicks}",
            _requestId,
            _sessionId,
            _item.Id,
            phase,
            _lifetime.ElapsedMilliseconds,
            Interlocked.Read(ref _transferredBytes),
            Interlocked.Read(ref _currentPositionTicks),
            _durationTicks,
            _totalLength,
            _canSeekForDiagnostics,
            _requestAborted.IsCancellationRequested,
            session?.NowPlayingItem?.Id,
            session?.PlayState?.PositionTicks);
    }

    private void ReportProgress(string reason, long positionTicks)
    {
        var sequence = Interlocked.Increment(ref _reportSequence);
        _logger.LogDebug("DLNA trace request={RequestId} session={SessionId} report={Sequence} action=OnPlaybackProgress reason={Reason} ticks={Ticks}", _requestId, _sessionId, sequence, reason, positionTicks);
        LogState("report-before");
        var task = _sessionManager.OnPlaybackProgress(new PlaybackProgressInfo
        {
            ItemId = _item.Id,
            SessionId = _sessionId,
            PositionTicks = positionTicks,
            IsPaused = false
        });
        _ = ObserveReportAsync(task, sequence);
    }

    private async Task ObserveReportAsync(Task task, long sequence)
    {
        try
        {
            await task.ConfigureAwait(false);
            _logger.LogDebug("DLNA trace request={RequestId} session={SessionId} report={Sequence} completed", _requestId, _sessionId, sequence);
            LogState("report-after");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DLNA trace request={RequestId} session={SessionId} report={Sequence} failed", _requestId, _sessionId, sequence);
        }
    }

    public override void SetLength(long value) => _innerStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _innerStream.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing && System.Threading.Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                LogState("dispose-sync");
                _innerStream.Dispose();
            }
            finally
            {
                try
                {
                    LogState("dispose-final-report");

                    if (_currentPositionTicks > 0)
                    {
                        var userData = _userDataManager.GetUserData(_user, _item);
                        if (userData != null)
                        {
                            _userDataManager.UpdatePlayState(_item, userData, _currentPositionTicks);
                            _userDataManager.SaveUserData(_user, _item, userData, MediaBrowser.Model.Entities.UserDataSaveReason.PlaybackProgress, CancellationToken.None);
                            _logger.LogDebug("DLNA trace request={RequestId} session={SessionId} user={UserId} saved resumeTicks={ResumeTicks} played={Played} estimatedTicks={EstimatedTicks}", _requestId, _sessionId, _user.Id, userData.PlaybackPositionTicks, userData.Played, _currentPositionTicks);
                        }
                    }

                    ReportProgress("dispose", _currentPositionTicks);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DLNA trace request={RequestId} session={SessionId} disposal reporting failed", _requestId, _sessionId);
                }
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
                LogState("dispose-async");
                await _innerStream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    LogState("dispose-final-report");

                    if (_currentPositionTicks > 0)
                    {
                        var userData = _userDataManager.GetUserData(_user, _item);
                        if (userData != null)
                        {
                            _userDataManager.UpdatePlayState(_item, userData, _currentPositionTicks);
                            _userDataManager.SaveUserData(_user, _item, userData, MediaBrowser.Model.Entities.UserDataSaveReason.PlaybackProgress, CancellationToken.None);
                            _logger.LogDebug("DLNA trace request={RequestId} session={SessionId} user={UserId} saved resumeTicks={ResumeTicks} played={Played} estimatedTicks={EstimatedTicks}", _requestId, _sessionId, _user.Id, userData.PlaybackPositionTicks, userData.Played, _currentPositionTicks);
                        }
                    }

                    ReportProgress("dispose", _currentPositionTicks);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DLNA trace request={RequestId} session={SessionId} disposal reporting failed", _requestId, _sessionId);
                }
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
