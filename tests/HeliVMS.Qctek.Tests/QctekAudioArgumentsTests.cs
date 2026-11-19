using HeliVMS.Qctek.Live;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 即時影像音訊輸出的參數與「要不要掛」的判定。
/// </summary>
/// <remarks>
/// <para>
/// 這裡釘的兩件事都不會出現在畫面上，但下場完全不同：清單外的編碼被誤判成「有音訊」
/// 時，ffmpeg 對 <c>-map 0:a:0</c> 找不到串流會直接結束，整個頻道連<b>畫面</b>都沒有；
/// 反過來漏判只是聽不到聲音（<c>channels.listen_enabled</c> 是操作員明確打開的）。
/// </para>
/// <para>
/// 另外 <c>-an</c> 只屬於影像輸出——它排在 <c>-f h264 -</c> 之前，音訊的第二個輸出
/// 仍可 <c>-map 0:a:0</c>。若有人把 <c>-an</c> 移到最後，聲音會從此行起全部消失。
/// </para>
/// </remarks>
public sealed class QctekAudioArgumentsTests
{
    [Theory]
    [InlineData("pcm_alaw", true)]     // G.711 A-law：VIVOTEK 等常見
    [InlineData("pcm_mulaw", true)]    // G.711 µ-law
    [InlineData("aac", true)]
    [InlineData("PCM_ALAW", true)]     // ffprobe 大小寫不該影響判定
    [InlineData("pcm_s16le", true)]
    [InlineData(null, false)]          // 沒有音軌
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("hdmv_pgs_subtitle", false)] // 字幕軌：不是音訊，掛了會讓 ffmpeg 失敗
    [InlineData("speex", false)]             // 不在白名單：寧可沒聲音，也不要黑屏
    public void 只有白名單內的音軌才會掛上音訊輸出(string? codec, bool expected) =>
        Assert.Equal(expected, QctekAudioArguments.ShouldAttach(codec));

    [Fact]
    public void 音訊輸出是第二個輸出路徑且接到本機UDP()
    {
        var arguments = QctekAudioArguments.Build(45678).ToList();

        // `-map 0:a:0` 是「這個輸出」的映射；沒有它 ffmpeg 會把影像也複製一份進音訊輸出。
        Assert.Equal("0:a:0", arguments[arguments.IndexOf("-map") + 1]);
        Assert.Equal("pcm_mulaw", arguments[arguments.IndexOf("-acodec") + 1]);
        Assert.Equal("mulaw", arguments[arguments.IndexOf("-f") + 1]);
        Assert.Equal("8000", arguments[arguments.IndexOf("-ar") + 1]);
        Assert.Equal("1", arguments[arguments.IndexOf("-ac") + 1]);

        // 即時聲音不能積在 AVIO 緩衝裡（8 kHz 單聲道可積近一秒，聽起來慢半拍）。
        Assert.Equal("1", arguments[arguments.IndexOf("-flush_packets") + 1]);

        // 接收端在同一台機器：位址只能是 loopback，埠就是已綁定的那一個。
        Assert.Equal($"udp://{QctekAudioArguments.LoopbackHost}:45678", arguments[^1]);
        Assert.True(QctekAudioArguments.IsAudioOutputUrl(arguments[^1]));
        Assert.False(QctekAudioArguments.IsAudioOutputUrl("-"));
    }

    [Fact]
    public void 每秒八包且一包一千位元組與真機一致()
    {
        // 真機 906 個 type=2 幀的 payload 全是 1000 bytes、約 8 包/秒（125ms）。
        // 長度或節奏變了，QCC 的播放速度就跟著變——聲音忽快忽慢，畫面卻完全正常。
        Assert.Equal(8, QctekAudioArguments.FramesPerSecond);
        Assert.Equal(1000, QctekAudioArguments.FrameLength);
        Assert.Equal(125, QctekAudioArguments.FrameIntervalMilliseconds);
        Assert.Equal(8000, QctekAudioArguments.SampleRate);

        // 8 kHz × 125ms × 1 byte（µ-law）＝ 1000 bytes。
        Assert.Equal(
            QctekAudioArguments.FrameLength,
            QctekAudioArguments.SampleRate * QctekAudioArguments.FrameIntervalMilliseconds / 1000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void 埠號不合法時直接擋下來(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QctekAudioArguments.Build(port));

    [Fact]
    public void 音訊幀的型別與編碼與影像幀不同()
    {
        var frame = QctekLivePackets.AudioFrame(3, 1_799_000_000, 999_999, new byte[1000]);
        var media = frame.AsSpan(8, 0x38);

        Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(media));
        Assert.Equal(200, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(media[0x04..]));
        Assert.Equal(1000, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(media[0x08..]));

        // 音訊沒有 SPS/PPS：block1/block2 必須是 0（客戶端只以 block1 > 0 認關鍵影格）。
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(media[0x0c..]));
        Assert.Equal(0, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(media[0x10..]));
    }
}
