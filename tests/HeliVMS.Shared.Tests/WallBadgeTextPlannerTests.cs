using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 監看牆徽章的組字。徽章是操作員從畫面判斷「這台錄影方式是什麼、現在有沒有在錄」的
/// 唯一依據，所以每一種組合都要有釘死的結果——特別是「沒錄的原因」不能蓋掉錄影方式。
/// </summary>
public class WallBadgeTextPlannerTests
{
    [Fact]
    public void 徽章會同時寫出錄影方式與錄影中記號()
    {
        Assert.Equal("車道 1 · 全時 ●REC",
            WallBadgeTextPlanner.Compose("車道 1", "全時", recording: true, blockLabel: null));
    }

    [Fact]
    public void 沒錄時徽章會寫出沒錄的原因()
    {
        // 排程錄影在時段外是正常狀態，不是故障——但操作員要能從畫面分辨它與「全時卻沒錄」。
        Assert.Equal("車道 1 · 排程（排程外）",
            WallBadgeTextPlanner.Compose("車道 1", "排程", recording: false, blockLabel: "排程外"));
    }

    [Fact]
    public void 查不到錄影方式時只寫名稱不會亂猜()
    {
        Assert.Equal("車道 1",
            WallBadgeTextPlanner.Compose("車道 1", modeLabel: null, recording: false, blockLabel: null));
    }

    [Fact]
    public void 錄影中不會同時出現沒錄的原因()
    {
        // 兩句話同時出現時，操作員無法判斷哪一句才是真的（例如剛好有事件的瞬間）。
        Assert.Equal("車道 1 · 事件 ●REC",
            WallBadgeTextPlanner.Compose("車道 1", "事件", recording: true, blockLabel: "僅事件錄影"));
    }

    [Fact]
    public void 沒有名稱也沒有錄影方式時徽章是空的()
    {
        // 空字串＝呼叫端把徽章收起來，不是留一個半透明空殼蓋在畫面上。
        Assert.Equal(string.Empty,
            WallBadgeTextPlanner.Compose(string.Empty, modeLabel: null, recording: false, blockLabel: null));
    }

    [Fact]
    public void 沒錄也沒有原因時只寫名稱與錄影方式()
    {
        Assert.Equal("車道 1 · 手動",
            WallBadgeTextPlanner.Compose("車道 1", "手動", recording: false, blockLabel: null));
    }
}
