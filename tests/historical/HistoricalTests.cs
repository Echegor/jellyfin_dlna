using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

public class HistoricalTests
{
    [Theory]
    [InlineData("20477c2", false)]
    [InlineData("0ceed2a", false)]
    [InlineData("6a63e19", false)]
    [InlineData("f815c88", true)]
    public async Task ReplayImmediateTailProbe(string commit, bool reportsEnd)
    {
        var sessions = new Mock<ISessionManager>();
        var data = new Mock<IUserDataManager>();
        var user = new User("test", "auth", "reset");
        var item = new Video { Id = Guid.NewGuid(), RunTimeTicks = 53679570000 };
        data.Setup(x => x.GetUserData(user, item)).Returns(new UserItemData { Key = "test" });
        var type = typeof(HistoricalTests).Assembly.GetType("Historical.C" + commit + ".ProgressTrackingStream")!;
        using var inner = new SparseFile();
        var stream = (Stream)Activator.CreateInstance(type, inner, sessions.Object, data.Object, "session", item, user, 4300392594L, NullLogger.Instance)!;
        stream.Seek(4300336835, SeekOrigin.Begin);
        Assert.Equal(55759, await stream.ReadAsync(new byte[55759]));
        await stream.DisposeAsync();
        if (reportsEnd)
        {
            sessions.Verify(x => x.OnPlaybackProgress(It.Is<PlaybackProgressInfo>(p => p.PositionTicks == 53679570000)), Times.Exactly(2));
            sessions.Verify(x => x.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()), Times.Never);
        }
        else
        {
            sessions.Verify(x => x.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>()), Times.Never);
            sessions.Verify(x => x.OnPlaybackStopped(It.Is<PlaybackStopInfo>(p => p.PositionTicks == 0)), Times.Once);
        }
    }

    private sealed class SparseFile : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 4300392594;
        public override long Position { get; set; }
        public override long Seek(long offset, SeekOrigin origin) => Position = offset;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, Length - Position);
            Position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            int n = (int)Math.Min(buffer.Length, Length - Position);
            Position += n;
            return ValueTask.FromResult(n);
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
