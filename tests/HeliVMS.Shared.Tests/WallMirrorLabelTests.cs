using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 第二螢幕鏡像牆每格的左下角文字（<see cref="WallMirrorLabel"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 這一條字串是操作員在第二螢幕上唯一讀得到的資訊（主牆的徽章、讀數、時間軸都不在那裡），
/// 而它同時要回答三件事：哪一台、該不該在錄、現在幾點。少掉時刻的那一版問不出
/// 「這是不是現在的畫面」——回報時最常被追問的就是這一句。
/// </para>
/// </remarks>
public class WallMirrorLabelTests
{
    [Fact]
    public void 徽章文字送來後接上時刻()
    {
        var text = WallMirrorLabel.Compose(
            "CH01 · 全時 · ●REC",
            "CH01",
            new DateTime(2026, 11, 11, 18, 25, 32));

        Assert.Equal("CH01 · 全時 · ●REC · 18:25:32", text);
    }

    [Fact]
    public void 徽章還沒送來時退回頻道名稱()
    {
        // 剛開窗、或主牆那一秒還沒走到（UpdateBadges 一秒一次）：畫面不能是空的。
        Assert.Equal("CH07 · 09:00:01", WallMirrorLabel.Compose(string.Empty, "CH07", new DateTime(2026, 11, 11, 9, 0, 1)));
        Assert.Equal("CH07 · 09:00:01", WallMirrorLabel.Compose(null, "CH07", new DateTime(2026, 11, 11, 9, 0, 1)));
        Assert.Equal("CH07 · 09:00:01", WallMirrorLabel.Compose("   ", "CH07", new DateTime(2026, 11, 11, 9, 0, 1)));
    }

    [Fact]
    public void 兩者都沒有時整條留白()
    {
        // 沒有頻道的格位（鏡像 16 格、主牆 4 台）不可以印出「 · 18:25:32」這種半截字串。
        Assert.Equal(string.Empty, WallMirrorLabel.Compose(string.Empty, string.Empty, DateTime.Now));
        Assert.Equal(string.Empty, WallMirrorLabel.Compose(null, null, DateTime.Now));
    }
}
