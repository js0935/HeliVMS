using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 格內狀態文字要報哪一個解析度。現場回報「子碼流顯示 480x270，應該是 640x480」，
/// 而 480x270 是我們依格數縮下來的解碼尺寸、來源其實是 640x360——錯的不是數字，
/// 是它被當成「這支攝影機的解析度」講出來。
/// </summary>
public class LiveResolutionReportTests
{
    private const string Scaled = "{0}（解碼 {1}）";

    /// <summary>
    /// 來源與解碼尺寸相同（1–4 格不縮）時只寫一個數字。這是絕大多數情況，
    /// 多寫一個括號只會讓格子裡那行小字更擠。
    /// </summary>
    [Fact]
    public void 沒有縮小解碼時只顯示一個解析度()
    {
        Assert.Equal("640×360", LiveResolutionReport.Format(640, 360, 640, 360, Scaled));
        Assert.False(LiveResolutionReport.IsScaled(640, 360, 640, 360));
    }

    /// <summary>
    /// 縮小解碼時兩個都要寫：來源是攝影機的事實（操作員拿它去對攝影機設定），
    /// 解碼尺寸是我們的決定（可以被質疑與調整）。少寫任何一個都會誤導人。
    /// </summary>
    [Fact]
    public void 縮小解碼時會同時寫出來源與解碼尺寸()
    {
        Assert.Equal("640×360（解碼 480×270）", LiveResolutionReport.Format(640, 360, 480, 270, Scaled));
        Assert.True(LiveResolutionReport.IsScaled(640, 360, 480, 270));
    }

    /// <summary>
    /// 4:3 的來源（640x480）在多分割下是 480x360，而 16:9 的來源是同寬的 480x270——
    /// 同一面牆上兩種高度並存不是 bug，是兩種來源。兩個都要照樣標出來。
    /// </summary>
    [Fact]
    public void 四比三與十六比九的來源各自標註自己的解碼尺寸()
    {
        Assert.Equal("640×480（解碼 480×360）", LiveResolutionReport.Format(640, 480, 480, 360, Scaled));
        Assert.Equal("640×360（解碼 480×270）", LiveResolutionReport.Format(640, 360, 480, 270, Scaled));
    }

    /// <summary>
    /// 還沒探測到來源時不可以把解碼尺寸冒充成來源，也不可以編一個：只報目前看到的這一張。
    /// </summary>
    [Fact]
    public void 來源未知時只報目前的影格尺寸()
    {
        Assert.Equal("480×270", LiveResolutionReport.Format(0, 0, 480, 270, Scaled));
        Assert.False(LiveResolutionReport.IsScaled(0, 0, 480, 270));
    }

    /// <summary>還沒有影格時整段省略，不寫 0x0（那看起來像一個真的尺寸）。</summary>
    [Fact]
    public void 還沒有影格時回傳空字串()
    {
        Assert.Equal(string.Empty, LiveResolutionReport.Format(640, 360, 0, 0, Scaled));
    }

    /// <summary>
    /// 文案由呼叫端（i18n）提供；沒有文案時退回英文版，而不是拋例外——
    /// 這一格是每秒重畫的狀態文字，為了缺一個翻譯而讓整個巡檢迴圈爆掉才是最糟的結果。
    /// </summary>
    [Fact]
    public void 缺少文案時退回預設排版而不是拋例外()
    {
        Assert.Equal("640×360 (480×270)", LiveResolutionReport.Format(640, 360, 480, 270, string.Empty));
    }
}
