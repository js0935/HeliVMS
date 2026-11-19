namespace HeliVMS.Qctek.Tests;

/// <summary>
/// FRAM 微秒欄位的<b>呼叫端</b>口徑：即時影像與遠端回放都要走
/// <see cref="HeliVMS.Qctek.Live.QctekTimestamps.MicrosecondOfSecond(DateTime)"/>。
/// </summary>
/// <remarks>
/// <para>
/// 這裡只剩來源契約可用：通道的牆鐘是 <c>DateTimeOffset.UtcNow</c>，沒有可注入的時鐘，
/// 而錯誤值（只取毫秒之內的 0–999）與正確值（0–999999）在畫面上分不出來——兩邊日誌都乾淨，
/// 只有 QCC 的調速容差會把非關鍵幀整包丟掉。換算函式本身由
/// <see cref="QctekTimestampsTests"/> 行為測試釘住，這裡釘的是「兩條路徑都還在用它」。
/// </para>
/// <para>
/// 不是「有呼叫就好」：即時影像那三處（視訊、開流那一包音訊、之後每 125ms 的音訊）
/// 都必須是同一份換算，只改一處的症狀是音訊與影像各差一個數量級。
/// </para>
/// </remarks>
public sealed class QctekTimestampCallSiteTests
{
    [Fact]
    public void 即時影像的視訊與音訊微秒欄位都走共用換算()
    {
        var live = ReadSource("src/HeliVMS.Qctek/Live/QctekLiveSession.cs");

        // 三個寫出點：Tick 的視訊、PumpAudio 的持續音訊、StartChannel 的開流音訊。
        Assert.Equal(3, Count(live, "QctekTimestamps.MicrosecondOfSecond(wallClock)"));
        Assert.DoesNotContain("UtcDateTime.Microsecond", live);
    }

    [Fact]
    public void 遠端回放的微秒欄位也走共用換算()
    {
        var playback = ReadSource("src/HeliVMS.Qctek/Live/QctekPlaybackSession.cs");

        Assert.Contains("QctekTimestamps.MicrosecondOfSecond(frameUtc)", playback);
        Assert.DoesNotContain("frameUtc.Microsecond", playback);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    /// <summary>從測試輸出目錄往上找 <c>HeliVMS.slnx</c>（與 App.Tests 的 SourceContract 同一規則）。</summary>
    private static string ReadSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeliVMS.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
    }
}
