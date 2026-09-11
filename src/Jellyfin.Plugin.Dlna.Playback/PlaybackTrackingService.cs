using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Dlna.Playback;

/// <summary>Owns inferred playback sessions independently of HTTP request scopes.</summary>
public sealed class PlaybackTrackingService : BackgroundService, IPlaybackReporter
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PlaybackTrackingService> _logger;
    private readonly ConcurrentDictionary<(Guid User, string Device), string> _sessions = new();

    /// <summary>Initializes a new instance of the <see cref="PlaybackTrackingService"/> class.</summary>
    /// <param name="scopes">Scope factory for independent reporting lifetimes.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public PlaybackTrackingService(IServiceScopeFactory scopes, ILogger<PlaybackTrackingService> logger)
    {
        _scopes = scopes;
        _logger = logger;
        Tracker = new PlaybackTracker(this, TimeProvider.System, (request, decision) => logger.LogDebug("DLNA trace request={RequestId} decision={Decision}", request, decision));
    }

    /// <summary>Gets the coordinator shared by video requests.</summary>
    public PlaybackTracker Tracker { get; }

    /// <inheritdoc />
    public async Task StartAsync(PlaybackIdentity identity, long positionTicks)
    {
        using var scope = _scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionManager>();
        var user = scope.ServiceProvider.GetRequiredService<IUserManager>().GetUserById(identity.UserId)
            ?? throw new InvalidOperationException("DLNA playback user no longer exists.");
        var session = await sessions.LogSessionActivity("DLNA", "1.0.0", identity.DeviceId, "DLNA Client", identity.RemoteAddress, user).ConfigureAwait(false);
        _sessions[(identity.UserId, identity.DeviceId)] = session.Id;
        // A failed event subscriber may throw after Jellyfin has already applied start.
        // Checking the resulting state makes a retry safe in that case.
        if (session.NowPlayingItem?.Id != identity.ItemId)
        {
            try
            {
                await sessions.OnPlaybackStart(new PlaybackStartInfo
                {
                    ItemId = identity.ItemId,
                    SessionId = session.Id,
                    MediaSourceId = identity.MediaSourceId,
                    PositionTicks = positionTicks,
                    PlayMethod = PlayMethod.DirectStream,
                    IsPaused = false
                }).ConfigureAwait(false);
            }
            finally
            {
                // The coordinator owns its estimated clock; a second automatic clock
                // would keep advancing after reads stop and race with seek updates.
                session.StopAutomaticProgress();
            }
        }

        _logger.LogDebug("DLNA trace session={SessionId} item={ItemId} action=session-attached estimatedTicks={Ticks}", session.Id, identity.ItemId, positionTicks);
    }

    /// <inheritdoc />
    public async Task ProgressAsync(PlaybackIdentity identity, long positionTicks)
    {
        using var scope = _scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionManager>();
        await StartAsync(identity, positionTicks).ConfigureAwait(false);
        var sessionId = _sessions[(identity.UserId, identity.DeviceId)];
        var session = sessions.Sessions.First(candidate => candidate.Id == sessionId);
        session.LastPlaybackCheckIn = DateTime.UtcNow;

        // Automated updates change live session state without calling UpdatePlayState,
        // whose completion rules must not run on byte-derived estimates.
        await sessions.OnPlaybackProgress(
            new PlaybackProgressInfo
            {
            ItemId = identity.ItemId,
            SessionId = sessionId,
            MediaSourceId = identity.MediaSourceId,
            PositionTicks = positionTicks,
            PlayMethod = PlayMethod.DirectStream,
            IsPaused = false
            },
            true).ConfigureAwait(false);
        SaveResume(scope.ServiceProvider, identity, positionTicks);
        _logger.LogDebug("DLNA trace session={SessionId} item={ItemId} action=progress-completed estimatedTicks={Ticks} nowPlaying={NowPlaying} sessionTicks={SessionTicks}", sessionId, identity.ItemId, positionTicks, session.NowPlayingItem?.Id, session.PlayState?.PositionTicks);
    }

    /// <inheritdoc />
    public async Task StopAsync(PlaybackIdentity identity, long positionTicks)
    {
        using var scope = _scopes.CreateScope();
        SaveResume(scope.ServiceProvider, identity, positionTicks);
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionManager>();
        if (_sessions.TryGetValue((identity.UserId, identity.DeviceId), out var sessionId))
        {
            // HTTP cannot confirm playback completion. End this synthetic session,
            // rather than emit a PlaybackStopped event with fabricated completion data.
            await sessions.ReportSessionEnded(sessionId).ConfigureAwait(false);
            _sessions.TryRemove((identity.UserId, identity.DeviceId), out _);
            _logger.LogDebug("DLNA trace session={SessionId} item={ItemId} action=session-ended estimatedTicks={Ticks}", sessionId, identity.ItemId, positionTicks);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await Tracker.SweepAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DLNA trace action=session-maintenance-failed; will retry");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            try
            {
                await Tracker.SweepAsync(shutdown: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DLNA trace action=shutdown-cleanup-failed");
            }
        }
    }

    private void SaveResume(IServiceProvider services, PlaybackIdentity identity, long positionTicks)
    {
        var user = services.GetRequiredService<IUserManager>().GetUserById(identity.UserId);
        var item = services.GetRequiredService<ILibraryManager>().GetItemById(identity.ItemId);
        if (user is null || item is null)
        {
            return;
        }

        var manager = services.GetRequiredService<IUserDataManager>();
        var data = manager.GetUserData(user, item);
        if (data is null)
        {
            return;
        }

        var configuration = services.GetRequiredService<IServerConfigurationManager>().Configuration;
        var position = Math.Clamp(positionTicks, 0, identity.DurationTicks);
        data.PlaybackPositionTicks = identity.DurationTicks < TimeSpan.FromSeconds(configuration.MinResumeDurationSeconds).Ticks
            || (double)position / identity.DurationTicks * 100 < configuration.MinResumePct ? 0 : position;
        // Preserve Played and PlayCount. Reading the end of a file is not completion.
        manager.SaveUserData(user, item, data, UserDataSaveReason.PlaybackProgress, CancellationToken.None);
        _logger.LogDebug("DLNA trace item={ItemId} user={UserId} action=resume-saved resumeTicks={Ticks} played={Played}", identity.ItemId, identity.UserId, data.PlaybackPositionTicks, data.Played);
    }
}
