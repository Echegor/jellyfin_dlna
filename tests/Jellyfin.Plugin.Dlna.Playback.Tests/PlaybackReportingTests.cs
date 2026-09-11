using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Dlna.Playback.Tests;

public class PlaybackReportingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EstimatesAtEndPreserveWatchedFlagAndCleanupDoesNotAssertCompletion(bool alreadyPlayed)
    {
        var user = new User("test", "auth", "reset") { Id = Guid.NewGuid() };
        var item = new Video { Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromMinutes(90).Ticks };
        var data = new UserItemData { Key = "item", Played = alreadyPlayed, PlayCount = 3 };
        var users = new Mock<IUserManager>();
        users.Setup(x => x.GetUserById(user.Id)).Returns(user);
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetItemById(item.Id)).Returns(item);
        var userData = new Mock<IUserDataManager>();
        userData.Setup(x => x.GetUserData(user, item)).Returns(data);
        var config = new Mock<IServerConfigurationManager>();
        config.SetupGet(x => x.Configuration).Returns(new ServerConfiguration());
        var sessions = new Mock<ISessionManager>();
        await using var session = new SessionInfo(sessions.Object, NullLogger.Instance) { Id = "session" };
        sessions.SetupGet(x => x.Sessions).Returns(new[] { session });
        sessions.Setup(x => x.LogSessionActivity("DLNA", "1.0.0", "phone", "DLNA Client", null, user)).ReturnsAsync(session);
        sessions.Setup(x => x.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()))
            .Callback<PlaybackStartInfo>(info => session.NowPlayingItem = new BaseItemDto { Id = info.ItemId })
            .Returns(Task.CompletedTask);
        sessions.Setup(x => x.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), true)).Returns(Task.CompletedTask);
        sessions.Setup(x => x.ReportSessionEnded(session.Id)).Returns(ValueTask.CompletedTask);
        using var provider = new ServiceCollection()
            .AddSingleton(users.Object).AddSingleton(library.Object).AddSingleton(userData.Object)
            .AddSingleton(config.Object).AddSingleton(sessions.Object).BuildServiceProvider();
        using var service = new PlaybackTrackingService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PlaybackTrackingService>.Instance);
        var identity = new PlaybackIdentity(user.Id, "phone", item.Id, "source", null, item.RunTimeTicks.Value, 1000000);
        await service.StartAsync(identity, 0);
        await service.ProgressAsync(identity, item.RunTimeTicks.Value);
        await service.StopAsync(identity, item.RunTimeTicks.Value);
        Assert.Equal(alreadyPlayed, data.Played);
        Assert.Equal(3, data.PlayCount);
        sessions.Verify(x => x.OnPlaybackStart(It.IsAny<PlaybackStartInfo>()), Times.Once);
        sessions.Verify(x => x.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), true), Times.Once);
        sessions.Verify(x => x.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Never);
        sessions.Verify(x => x.ReportSessionEnded(session.Id), Times.Once);
        userData.Verify(x => x.UpdatePlayState(It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<long>()), Times.Never);
    }

    [Fact]
    public async Task ShortTailStreamAndEmptyResponseDoNotStartPlayback()
    {
        var reporter = new Mock<IPlaybackReporter>(MockBehavior.Strict);
        var tracker = new PlaybackTracker(reporter.Object, TimeProvider.System, (_, _) => { });
        var identity = new PlaybackIdentity(Guid.NewGuid(), "phone", Guid.NewGuid(), "source", null, TimeSpan.FromMinutes(90).Ticks, 100000);
        using var inner = new MemoryStream(new byte[100000]);
        await using (var stream = new ProgressTrackingStream(inner, tracker, identity, "tail", NullLogger.Instance, CancellationToken.None))
        {
            stream.Seek(99900, SeekOrigin.Begin);
            Assert.Equal(100, await stream.ReadAsync(new byte[1000]));
        }

        await using (var empty = new ProgressTrackingStream(new MemoryStream(), tracker, identity, "empty", NullLogger.Instance, CancellationToken.None))
        {
        }

        reporter.VerifyNoOtherCalls();
    }
}
