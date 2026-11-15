using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 換解碼寬度時「靜默換手」的寬限長度（<see cref="SilentHandoffGrace"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 這條線錯了不會有任何錯誤訊息：設太短＝快的那一半每格都閃一下「連線中」（現場回報的
/// 「切換分割攝影機會出現重新連線」），設太長＝慢的那一半定格卻標著「串流中」。
/// 兩端都是靜默的，所以上下限與放大倍數全部釘在這裡。
/// </para>
/// </remarks>
public class SilentHandoffGraceTests
{
    [Fact]
    public void 還沒量到啟動延遲時退回下限()
    {
        // 第一次連線還沒有「連線中→串流中」的樣本。憑空猜一個比設定值更大的數字更糟：
        // 那會讓第一次換手就瞞著操作員一段沒有依據的時間。
        var grace = SilentHandoffGrace.Resolve(TimeSpan.FromSeconds(2.5), measuredStartupLatency: null);

        Assert.Equal(TimeSpan.FromSeconds(2.5), grace);
    }

    [Fact]
    public void 量到的延遲為零也視同還沒量到()
    {
        // 時鐘精度不足或樣本被截斷時會是 0；那不是「換手瞬間完成」的證據。
        var grace = SilentHandoffGrace.Resolve(TimeSpan.FromSeconds(1), TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(1), grace);
    }

    [Fact]
    public void 寬限依量到的啟動延遲放大兩倍()
    {
        // 留一次抖動的餘裕。原本固定 2.5 秒的行為在「換手要 5 秒」的攝影機上，
        // 會在其完成前就照實發布「連線中」——現場回報的「切換分割會重新連線」。
        var grace = SilentHandoffGrace.Resolve(
            TimeSpan.FromSeconds(2.5),
            TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(4), grace);
    }

    [Fact]
    public void 寬限不會超過上限()
    {
        // 上限必須低於健康檢查的停滯判定（10 秒）：靜默撐得比停滯判定還久，就會出現
        // 「格位寫著串流中、健康檢查卻正在重啟它」的矛盾畫面。
        var grace = SilentHandoffGrace.Resolve(
            TimeSpan.FromSeconds(2.5),
            TimeSpan.FromSeconds(9));

        Assert.Equal(SilentHandoffGrace.DefaultCeiling, grace);
        Assert.True(
            SilentHandoffGrace.DefaultCeiling < TimeSpan.FromSeconds(10),
            "靜默寬限必須短於停滯判定 10 秒");
    }

    [Fact]
    public void 快機的寬限不會被放大到比下限還久()
    {
        // 區網攝影機換手 0.5 秒；放大後仍是 1 秒，比下限短，所以取下限。
        var grace = SilentHandoffGrace.Resolve(
            TimeSpan.FromSeconds(2.5),
            TimeSpan.FromSeconds(0.5));

        Assert.Equal(TimeSpan.FromSeconds(2.5), grace);
    }

    [Fact]
    public void 上限低於下限時以下限為準()
    {
        // 呼叫端（測試）可以把寬限調得很短，但上限不能反過來把下限吃掉。
        var grace = SilentHandoffGrace.Resolve(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            ceiling: TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(2), grace);
    }

    [Fact]
    public void 負的下限視為零而不是負值()
    {
        // Task.Delay 收到負值會直接回傳完成；那會讓靜默在一瞬間就到期。
        var grace = SilentHandoffGrace.Resolve(
            TimeSpan.FromSeconds(-1),
            measuredStartupLatency: null,
            ceiling: TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.Zero, grace);
    }
}
