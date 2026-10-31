namespace HeliVMS.App.Tests;

/// <summary>
/// 螢幕虛擬鍵盤（<c>Osk.cs</c>）的原始碼契約。
/// </summary>
/// <remarks>
/// <para>
/// 歷史（M292）：自製鍵盤面板曾因拖曳守衛寫成 <c>e.OriginalSource is Button</c> 而
/// 完全打不出字（點按鈕時 OriginalSource 是範本內的 Border，守衛永遠不成立，每個
/// 按鍵都誤觸 DragMove 的模態迴圈）。後來整個面板移除，改為呼叫系統 <c>osk.exe</c>
/// ——鍵帽、IME、各國配置都是 Windows 的維護成本，本程式只剩「還焦點、叫起來」。
/// </para>
/// <para>
/// 兩個用契約釘住的細節：
/// </para>
/// <list type="bullet">
/// <item>osk 的按鍵送到「目前有焦點的控制項」。呼叫鈕被點下時 WPF 把焦點給按鈕，
/// 不先還給輸入框，osk 打的字會落在按鈕上——症狀是「小鍵盤沒作用」。</item>
/// <item>32 位元程序在 64 位元 Windows 上，System32 會被轉送到 SysWOW64（那裡沒有
/// osk.exe），必須走 Sysnative 虛擬別名，否則啟動直接 Win32Exception。</item>
/// </list>
/// </remarks>
public sealed class OskContractTests
{
    private const string Code = "src/HeliVMS.App/Osk.cs";

    [Fact]
    public void 呼叫鈕會聚焦目標輸入框並啟動系統小鍵盤()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "ToggleCore");
        Assert.NotEqual(string.Empty, body);

        // osk 的按鍵送到有焦點的控制項：先還焦點，再啟動。
        Assert.Contains("target.Focus()", body);
        Assert.Contains("Process.Start(", body);
        Assert.Contains("ResolveOskPath()", body);
    }

    [Fact]
    public void 三十二位元程序要走Sysnative找osk()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "ResolveOskPath");
        Assert.NotEqual(string.Empty, body);

        Assert.Contains("Environment.Is64BitProcess", body);
        Assert.Contains("Sysnative", body);
        Assert.Contains("osk.exe", body);
    }

    [Fact]
    public void 啟用狀態仍控制呼叫鈕的顯示()
    {
        var source = SourceContract.Read(Code);

        // 設定頁的「觸控模式」開關只透過 Enabled 生效：關閉時呼叫鈕整批藏起來。
        // Enabled 是屬性（沒有括號），不能走 BodyOf，直接對全文斷言。
        Assert.Contains("public static bool Enabled", source, StringComparison.Ordinal);
        Assert.Contains("_enabled = value", source, StringComparison.Ordinal);
        Assert.Contains("foreach (var b in _callButtons)", source, StringComparison.Ordinal);
        Assert.Contains("public static Visibility CallVisibility", source, StringComparison.Ordinal);
    }

    [Fact]
    public void 不再內建自製鍵盤面板()
    {
        var code = SourceContract.Read(Code);

        // 面板、共用版面與按鍵寫入邏輯都是歷史遺跡，不得回魂：
        // 回魂等於同時有兩套鍵盤行為（osk 與面板）搶同一批呼叫鈕。
        Assert.DoesNotContain("OskPanel", code);
        Assert.DoesNotContain("OnScreenKeyboardLayout", code);
        Assert.DoesNotContain("AppendInput", code);
        Assert.DoesNotContain("DragMove", code);
    }
}
