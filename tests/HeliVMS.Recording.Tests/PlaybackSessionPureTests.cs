using HeliVMS.Recording;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

public class PlaybackSessionPureTests
{
    [Fact]
    public void 建立時預設非播放中()
    {
        var seg = new SegmentRecord
        {
            Id = 1,
            ChannelId = 1,
            Stream = "main",
            StartUtc = DateTime.UtcNow.AddHours(-1),
            EndUtc = DateTime.UtcNow,
            FilePath = "dummy.mp4",
            SizeBytes = 1000,
            DurationSec = 60
        };
        var sess = new PlaybackSession(seg);
        Assert.False(sess.IsPlaying);
        Assert.Equal(1.0, sess.Speed);
        Assert.Equal(0, sess.FrameIndex);
        Assert.Equal(0, sess.FramesRead);
    }

    [Fact]
    public async Task DisposeAsync_多次呼叫不會丟例外()
    {
        var seg = new SegmentRecord
        {
            Id = 1,
            ChannelId = 1,
            Stream = "main",
            StartUtc = DateTime.UtcNow.AddHours(-1),
            EndUtc = DateTime.UtcNow,
            FilePath = "dummy.mp4",
            SizeBytes = 1000,
            DurationSec = 60
        };
        var sess = new PlaybackSession(seg);
        await sess.DisposeAsync();
        await sess.DisposeAsync();
    }
}