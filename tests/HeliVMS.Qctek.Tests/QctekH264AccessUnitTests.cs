using HeliVMS.Qctek.Live;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// Annex-B 切分與 AU 分組：餵進來的是 ffmpeg <c>-f h264</c> 的任意分塊輸出，
/// 出去的是「可打一個 FRAM」的 AU。
/// <para>
/// 測試用的 NAL 是手工拼的（type 正確、內容隨意）——切分器只看 start code 與
/// NAL header 的 type/first_mb 位元，不在乎語法是否真的可解碼。關鍵的兩個
/// 判準都有真機證據：SPS/PPS 抽到 block1/block2 且帶 4-byte start code
/// （<c>SplitIFrame_H264</c>）、first slice 以「slice header 首位元組最高位元＝1」
/// 判（<c>first_mb_in_slice</c> 的 ue(v) 值 0 編碼成「1」）。
/// </para>
/// </summary>
public class QctekH264AccessUnitTests
{
    private static byte[] Sc3(params byte[] content)
    {
        var nal = new byte[content.Length + 3];
        nal[2] = 1;
        content.CopyTo(nal, 3);
        return nal;
    }

    private static byte[] Sc4(params byte[] content)
    {
        var nal = new byte[content.Length + 4];
        nal[3] = 1;
        content.CopyTo(nal, 4);
        return nal;
    }

    /// <summary>SPS（type 7）。</summary>
    private static byte[] Sps() => Sc4(0x67, 0x42, 0x00, 0x1f);

    /// <summary>PPS（type 8）。</summary>
    private static byte[] Pps() => Sc4(0x68, 0xce, 0x3c, 0x80);

    /// <summary>IDR slice（type 5；first_mb_in_slice＝0 → 首位元組最高位元＝1）。</summary>
    private static byte[] Idr() => Sc4(0x65, 0x88, 0x84, 0x00, 0x11);

    /// <summary>P slice（type 1；first_mb_in_slice＝0）。</summary>
    private static byte[] Slice() => Sc4(0x41, 0x9a, 0x00, 0x22);

    /// <summary>同一影像的第二個 slice（first_mb_in_slice ≠ 0 → 首位元組以 0 開頭）。</summary>
    private static byte[] ContinuationSlice() => Sc4(0x41, 0x00, 0x33);

    [Fact]
    public void 關鍵影格的SPS與PPS抽進block並帶四字元組startcode()
    {
        var grouper = new QctekH264AccessUnitGrouper();
        var units = grouper.Append([.. Sps(), .. Pps(), .. Idr()]);
        Assert.Empty(units); // AU 要到「下一個 first slice」或 Flush 才確定結束

        var done = grouper.Flush();
        var au = Assert.Single(done);

        Assert.True(au.Keyframe);
        Assert.Equal(Sps(), au.Block1);  // 4-byte start code ＋ NAL（真機 SplitIFrame_H264 的形狀）
        Assert.Equal(Pps(), au.Block2);
        Assert.Equal(Idr(), au.Video);   // SPS/PPS 不重複出現在 video
    }

    [Fact]
    public void 非關鍵影格的block1與block2是空的()
    {
        var grouper = new QctekH264AccessUnitGrouper();
        // IDR AU 在下一個 first slice 到達時交出；非關鍵 AU 要到再下一個 first slice。
        grouper.Append([.. Idr(), .. Slice(), .. Slice()]);
        var done = grouper.Append([.. Slice()]);

        var p = Assert.Single(done);
        Assert.False(p.Keyframe);
        Assert.Empty(p.Block1);
        Assert.Empty(p.Block2);
        Assert.Equal(Slice(), p.Video);
    }

    [Fact]
    public void 多slice影像的續片屬於同一個存取單元()
    {
        var grouper = new QctekH264AccessUnitGrouper();
        var done = new List<QctekAccessUnit>();
        done.AddRange(grouper.Append([.. Sps(), .. Pps(), .. Idr()]));
        done.AddRange(grouper.Append([.. Slice(), .. ContinuationSlice()]));
        done.AddRange(grouper.Flush());

        Assert.Equal(2, done.Count); // IDR 一個 AU、P（兩個 slice）一個 AU

        var p = done[1];
        Assert.False(p.Keyframe);

        // video 保持 Annex-B 原樣（兩個 slice 各帶自己的 start code）。
        var expected = new byte[Slice().Length + ContinuationSlice().Length];
        Slice().CopyTo(expected, 0);
        ContinuationSlice().CopyTo(expected, Slice().Length);
        Assert.Equal(expected, p.Video);
    }

    [Fact]
    public void 被切成兩半的startcode會等下一包而不是當成NAL內容()
    {
        var grouper = new QctekH264AccessUnitGrouper();
        var idr = Idr();

        // 第一包以 IDR 的 start code 前 2 bytes 收尾。
        var full = new byte[Sps().Length + Pps().Length + idr.Length];
        Sps().CopyTo(full, 0);
        Pps().CopyTo(full, Sps().Length);
        idr.CopyTo(full, Sps().Length + Pps().Length);
        var first = full[..^2];
        var second = full[^2..];

        grouper.Append(first);
        grouper.Append(second);

        var done = grouper.Flush();
        var au = Assert.Single(done);
        Assert.Equal(Sps(), au.Block1);
        Assert.Equal(Idr(), au.Video); // start code 完整保留在 video 開頭
    }

    [Fact]
    public void 分批餵入與一次餵入的分組結果相同()
    {
        byte[] stream = [.. Sps(), .. Pps(), .. Idr(), .. Slice(), .. Slice(), .. Idr(), .. Slice()];

        var whole = new QctekH264AccessUnitGrouper();
        var allUnits = whole.Append(stream);
        allUnits.AddRange(whole.Flush());

        var chunked = new QctekH264AccessUnitGrouper();
        var chunkedUnits = new List<QctekAccessUnit>();
        foreach (var chunk in stream.Chunk(5))
        {
            chunkedUnits.AddRange(chunked.Append(chunk));
        }

        chunkedUnits.AddRange(chunked.Flush());

        Assert.Equal(allUnits.Count, chunkedUnits.Count);
        for (var i = 0; i < allUnits.Count; i++)
        {
            Assert.Equal(allUnits[i].Keyframe, chunkedUnits[i].Keyframe);
            Assert.Equal(allUnits[i].Block1, chunkedUnits[i].Block1);
            Assert.Equal(allUnits[i].Block2, chunkedUnits[i].Block2);
            Assert.Equal(allUnits[i].Video, chunkedUnits[i].Video);
        }
    }

    [Fact]
    public void IDR前若還沒收到SPS則關鍵影格不附空的block()
    {
        var grouper = new QctekH264AccessUnitGrouper();
        grouper.Append([.. Idr(), .. Slice()]); // IDR AU 要到下一個 first slice（或串流結束）才交出
        var done = grouper.Flush();
        Assert.Equal(2, done.Count);

        var au = done[0];

        // 客戶端以 block1 > 0 當關鍵影格判準；空的關鍵影格宣告只會被當非關鍵丟棄，
        // 所以寧可不附（來源端 dump_extra 保證 SPS 會到）。
        Assert.True(au.Keyframe);
        Assert.Empty(au.Block1);
        Assert.Empty(au.Block2);
        Assert.Equal(Idr(), au.Video);
    }
}
