using HeliVMS.Recording;
namespace HeliVMS.Recording.Tests;
/// <summary>
/// KeyframeProbe.Parse 的契約：ffprobe 的 packet CSV 轉成 (時間, 位元組位移) 列。
/// </summary>
/// <remarks>
    /// 解析與行程分開的理由：這裡不需要 ffprobe 也能跑，CI 沒裝 ffmpeg 時守衛仍然有效。
    /// 真正的端到端探測可額外新增 SkippableFact 測試。
    /// </remarks>
public sealed class KeyframeProbeTests
{
    [Fact]
    public void 只取含K旗標的封包並依時間排序()
    {
        var stdout = "0.000000,K__,512\n1.000000,__D,1024\n2.000000,K__,8192\n3.000000,____,9000\n";
        var rows = KeyframeProbe.Parse(stdout);
        Assert.Equal([0.0, 2.0], rows.Select(r => r.TimeSec).ToArray());
        Assert.Equal([512, 8192], rows.Select(r => r.MoofOffset).ToArray());
    }
    [Fact]
    public void 輸入順序打亂時仍依時間排序()
    {
        var stdout = "4.0,K,4096\n0.0,K,512\n2.0,K,2048\n";
        var rows = KeyframeProbe.Parse(stdout);
        Assert.Equal([0.0, 2.0, 4.0], rows.Select(r => r.TimeSec).ToArray());
    }
    /// <summary>
    /// 同一時刻出現兩列（重複資料）必須去重：segment_keyframes 對 (segment_id, time_sec)
    /// 有 UNIQUE 約束，重複列會讓 Replace 的整批插入交易失敗，整段索引都建不起來。
    /// </summary>
    [Fact]
    public void 同一時刻的重複列只保留第一筆()
    {
        var stdout = "1.0,K,1024\n1.0,K,2048\n2.0,K,4096\n";
        var rows = KeyframeProbe.Parse(stdout);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1024, rows[0].MoofOffset);
        Assert.Equal(2.0, rows[1].TimeSec);
    }
    [Fact]
    public void 無法解析的列靜默跳過而不是讓整段失敗()
    {
        var stdout = "N/A,K,N/A\n0.5,K,256\nBROKEN_LINE\n\n2.5,__,3000\n1.5,K,1536\n";
        var rows = KeyframeProbe.Parse(stdout);
        Assert.Equal([0.5, 1.5], rows.Select(r => r.TimeSec).ToArray());
    }
    [Fact]
    public void 空輸出回空清單()
    {
        Assert.Empty(KeyframeProbe.Parse(string.Empty));
        Assert.Empty(KeyframeProbe.Parse("\n\n"));
    }

    [Fact]
    public void 零位移的怪值也會收進來()
    {
        var stdout = "0.000000,K,0\n";
        var rows = KeyframeProbe.Parse(stdout);
        Assert.Single(rows);
        Assert.Equal(0.0, rows[0].TimeSec);
        Assert.Equal(0, rows[0].MoofOffset);
    }
}