using HeliVMS.Qctek.Live;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 把 ffmpeg 送來的 µ-law 位元組流切成固定 1000-byte 的音訊幀。
/// </summary>
/// <remarks>
/// UDP 的切法與時間無關（一個 datagram 可能只有 200 bytes，也可能把好幾包黏在一起），
/// 而 QCC 把每一包當成 125ms 的聲音播放——長度錯了就是播放速度錯了，畫面卻完全正常。
/// </remarks>
public sealed class AudioFrameAssemblerTests
{
    private static byte[] Frame(byte value, int length = 1000) => Enumerable.Repeat(value, length).ToArray();

    [Fact]
    public void 湊滿一幀才交出而且跨多次Append拼接()
    {
        var assembler = new AudioFrameAssembler();

        assembler.Append(Frame(0x11, 400));
        Assert.Equal(0, assembler.QueuedFrames);
        Assert.Equal(400, assembler.PartialBytes);
        Assert.False(assembler.TryTakeFrame(new byte[1000]));

        assembler.Append(Frame(0x22, 600));
        Assert.Equal(1, assembler.QueuedFrames);
        Assert.Equal(0, assembler.PartialBytes);

        var frame = new byte[1000];
        Assert.True(assembler.TryTakeFrame(frame));
        Assert.Equal(Frame(0x11, 400), frame[..400]);
        Assert.Equal(Frame(0x22, 600), frame[400..]);

        // 取走之後就沒有了：再一次必須是 false（不能重播同一段聲音）。
        Assert.False(assembler.TryTakeFrame(frame));
    }

    [Fact]
    public void 一個Append剛好三幀時會依序交出三幀()
    {
        var assembler = new AudioFrameAssembler();
        assembler.Append([.. Frame(0x01), .. Frame(0x02), .. Frame(0x03)]);

        Assert.Equal(3, assembler.QueuedFrames);

        var frame = new byte[1000];
        Assert.True(assembler.TryTakeFrame(frame));
        Assert.All(frame, b => Assert.Equal(0x01, b));
        Assert.True(assembler.TryTakeFrame(frame));
        Assert.All(frame, b => Assert.Equal(0x02, b));
        Assert.True(assembler.TryTakeFrame(frame));
        Assert.All(frame, b => Assert.Equal(0x03, b));
    }

    [Fact]
    public void 排隊滿了會丟最舊的整幀而不是從半包處丟()
    {
        var assembler = new AudioFrameAssembler(capacity: 2);
        assembler.Append([.. Frame(0x01), .. Frame(0x02), .. Frame(0x03)]);

        // 半包處丟會讓之後每一幀都錯位（µ-law 是位元組對齊的取樣），所以只能整幀丟。
        Assert.Equal(1, assembler.DroppedFrames);
        Assert.Equal(2, assembler.QueuedFrames);
        Assert.Equal(0, assembler.PartialBytes);

        var frame = new byte[1000];
        Assert.True(assembler.TryTakeFrame(frame));
        Assert.All(frame, b => Assert.Equal(0x02, b)); // 最舊的 0x01 被丟掉
        Assert.True(assembler.TryTakeFrame(frame));
        Assert.All(frame, b => Assert.Equal(0x03, b));
    }

    [Fact]
    public void 緩衝區比一幀小會被擋下來()
    {
        var assembler = new AudioFrameAssembler();
        Assert.Throws<ArgumentException>(() => assembler.TryTakeFrame(new byte[999]));
    }

    [Fact]
    public void 清掉之後不會把舊聲音接到新畫面上()
    {
        var assembler = new AudioFrameAssembler();
        assembler.Append([.. Frame(0x01), .. Frame(0x02, 300)]);
        Assert.Equal(1, assembler.QueuedFrames);
        Assert.Equal(300, assembler.PartialBytes);

        assembler.Clear();

        Assert.Equal(0, assembler.QueuedFrames);
        Assert.Equal(0, assembler.PartialBytes);
        Assert.False(assembler.TryTakeFrame(new byte[1000]));
    }
}
