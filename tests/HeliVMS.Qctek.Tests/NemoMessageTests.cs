using System.Text;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>NEMO 訊框編解碼：固定 16 bytes 標頭、長度對齊、串流切分。</summary>
public class NemoMessageTests
{
    [Fact]
    public void 編碼把長度放在標頭並補空白到16byte()
    {
        // 與 QCV8001 內建回應字串逐字相同："NEMO 9" + 10 個空白 + 9 bytes payload。
        var frame = NemoMessage.EncodeText("OK NV7.1 ");

        Assert.Equal(16 + 9, frame.Length);
        Assert.Equal("NEMO 9          OK NV7.1 ", Encoding.ASCII.GetString(frame));
    }

    [Fact]
    public void 解碼QCV8001內建回應字串()
    {
        var frame = Encoding.ASCII.GetBytes("NEMO 11         OK QCV8001 ");

        Assert.True(NemoMessage.TryDecodeText(frame, out var payload, out var consumed));
        Assert.Equal("OK QCV8001 ", payload);
        Assert.Equal(frame.Length, consumed);
    }

    [Fact]
    public void 資料不足時不誤判為完整訊框()
    {
        var header = Encoding.ASCII.GetBytes("NEMO 11");
        Assert.False(NemoMessage.TryDecode(header, out _, out var consumed0));
        Assert.Equal(0, consumed0);

        var partial = Encoding.ASCII.GetBytes("NEMO 11         OK QCV");
        Assert.False(NemoMessage.TryDecode(partial, out _, out var consumed1));
        Assert.Equal(0, consumed1);
    }

    [Fact]
    public void 串接多個訊框可依序解析()
    {
        var stream = NemoMessage.EncodeText("OK QCV8001 ")
            .Concat(NemoMessage.EncodeText("ALIVE "))
            .ToArray();

        Assert.True(NemoMessage.TryDecodeText(stream, out var first, out var used1));
        Assert.Equal("OK QCV8001 ", first);

        Assert.True(NemoMessage.TryDecodeText(stream.AsSpan(used1), out var second, out var used2));
        Assert.Equal("ALIVE ", second);
        Assert.Equal(stream.Length, used1 + used2);
    }

    [Fact]
    public void 非NEMO開頭擲出格式錯誤讓呼叫端知道串流不同步()
    {
        var garbage = Encoding.ASCII.GetBytes("GET / HTTP/1.1   ");
        Assert.Throws<FormatException>(() => NemoMessage.TryDecode(garbage, out _, out _));
    }

    [Fact]
    public void 編解碼往返保持一致()
    {
        var payload = "CHECK_SERVER_VERSION  CLIENT_AUTH";
        var frame = NemoMessage.EncodeText(payload);
        Assert.True(NemoMessage.TryDecodeText(frame, out var decoded, out var consumed));
        Assert.Equal(payload, decoded);
        Assert.Equal(frame.Length, consumed);
    }
}
