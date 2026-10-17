namespace HeliVMS.Alarms.Tests;

/// <summary>
/// 事件中心「詳情」欄在地化（M322）。回歸風險：引擎寫的是機器可讀的英文/代碼 detail
/// （<c>person conf=0.92 bbox=(...)</c>、<c>duration=1234ms peak=45%</c>…），使用者看到的是
/// 一坨英文偵錯字串；改動 <c>EventDetailText</c> 的分支或 i18n 鍵就會打回原形。
/// 這裡逐格式驗證顯示字串，並確認儲存層格式（<see cref="DetectionDetail"/> 需解析）沒被動到。
/// </summary>
public class EventDetailTextTests
{
    [Fact]
    public void AI明細顯示繁中類別與信心度而非英文代碼()
    {
        var text = EventDetailText.Format("zh-Hant", "person conf=0.92 bbox=(0.30,0.40,0.20,0.50)");
        Assert.Equal("偵測到人（信心度 0.92）", text);
        Assert.DoesNotContain("conf=", text);
        Assert.DoesNotContain("bbox=", text);
    }

    [Theory]
    [InlineData("person", "人")]
    [InlineData("car", "車")]
    [InlineData("bus", "巴士")]
    [InlineData("truck", "卡車")]
    [InlineData("motorcycle", "機車")]
    [InlineData("bicycle", "腳踏車")]
    public void AI類別代碼對應繁中顯示名(string cls, string expected)
    {
        Assert.Equal(expected, EventDetailText.ClassLabel("zh-Hant", cls));
    }

    [Fact]
    public void 未知類別原樣顯示不會變成空白()
    {
        Assert.Equal("dinosaur", EventDetailText.ClassLabel("zh-Hant", "dinosaur"));
    }

    [Fact]
    public void 移動明細顯示繁中持續與峰值()
    {
        Assert.Equal("移動偵測：持續 1234 毫秒、峰值 45%", EventDetailText.Format("zh-Hant", "duration=1234ms peak=45%"));
    }

    [Fact]
    public void 音訊明細顯示繁中事件類型與分貝()
    {
        Assert.Equal(
            "音訊事件（突發）：峰值 -12.5 dB、平均 -30.1 dB、持續 800 毫秒",
            EventDetailText.Format("zh-Hant", "kind=burst;peak_db=-12.5;mean_db=-30.1;duration=800ms"));
    }

    [Fact]
    public void 鏡頭異常明細顯示繁中類型與亮度()
    {
        Assert.Equal(
            "鏡頭異常（全黑）：亮度 5、邊緣 3、持續 1500 毫秒",
            EventDetailText.Format("zh-Hant", "kind=blackout;brightness=5;edge=3;duration=1500ms"));
    }

    [Fact]
    public void IO輸入明細顯示繁中欄位()
    {
        Assert.Equal(
            "IO 輸入：裝置 門禁主機、接點 Door1、索引 2、優先等級 high",
            EventDetailText.Format("zh-Hant", "device=門禁主機;io=Door1;index=2;priority=high;state=on"));
    }

    [Fact]
    public void 分析模組已在地化的明細原樣顯示()
    {
        const string detail = "車道A：跨越界線（A→B）";
        Assert.Equal(detail, EventDetailText.Format("zh-Hant", detail));
    }

    [Fact]
    public void 無法辨識的明細原樣顯示()
    {
        Assert.Equal("something=weird", EventDetailText.Format("zh-Hant", "something=weird"));
    }

    [Fact]
    public void 空明細顯示破折號佔位()
    {
        Assert.Equal("—", EventDetailText.Format("zh-Hant", null));
        Assert.Equal("—", EventDetailText.Format("zh-Hant", ""));
    }

    [Fact]
    public void 英文語系輸出英文說明()
    {
        Assert.Equal(
            "Detected person (confidence 0.92)",
            EventDetailText.Format("en", "person conf=0.92 bbox=(0.30,0.40,0.20,0.50)"));
    }

    [Fact]
    public void 簡中語系輸出簡中說明()
    {
        Assert.Equal("检测到车（置信度 0.86）", EventDetailText.Format("zh-Hans", "car conf=0.86 bbox=(0.5,0.5,0.1,0.1)"));
    }

    [Fact]
    public void 儲存層格式未被在地化動到()
    {
        // 遮蔽功能靠 DetectionDetail.TryParse 解析原始 detail；顯示在地化不能改到寫入格式。
        const string raw = "person conf=0.92 bbox=(0.30,0.40,0.20,0.50)";
        _ = EventDetailText.Format("zh-Hant", raw);
        Assert.True(DetectionDetail.TryParse(raw, out var d));
        Assert.Equal("person", d.Class);
    }
}
