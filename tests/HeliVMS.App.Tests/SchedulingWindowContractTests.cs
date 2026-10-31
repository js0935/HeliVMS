using System.Text.RegularExpressions;
using Xunit;

namespace HeliVMS.App.Tests;

/// <summary>
/// 錄影排程視窗的契約（M10）。無邊框最大化視窗沒有系統標題列，
/// 「怎麼離開」必須有畫面上的答案；另外排程是「默默決定錄不錄影」的設定，
/// 表單不得存出一條實際上不會錄的規則。
/// </summary>
public sealed class SchedulingWindowContractTests
{
    private const string Code = "src/HeliVMS.App/SchedulingWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/SchedulingWindow.xaml";

    /// <summary>
    /// 無邊框最大化視窗必須有畫面上的出口：關閉鈕＋Esc。
    /// </summary>
    /// <remarks>
    /// 這個視窗原本整頁都是表單與清單，WindowStyle=None 又沒有任何關閉控制——
    /// 操作員唯一離開的方式是 Alt+F4，現場回報「沒有離開的按鈕」。其他無邊框視窗
    /// （Live／EventCenter…）都有右上角 ✕，這裡沿用同一個模式。
    /// </remarks>
    [Fact]
    public void 排程視窗有關閉鈕且Esc可離開()
    {
        var xaml = SourceContract.Read(Xaml);
        var source = SourceContract.Read(Code);

        // 關閉鈕：存在、有 AutomationId、掛到 OnCloseClicked（expression-bodied，本體是 => Close()）。
        Assert.Matches(
            @"<Button[^>]*x:Name=""CloseButton""[^>]*Click=""OnCloseClicked""",
            xaml);
        Assert.Contains(@"AutomationProperties.AutomationId=""SchedulingCloseButton""", xaml, StringComparison.Ordinal);
        Assert.Contains("Close()", SourceContract.BodyOf(source, "OnCloseClicked"));

        // Esc 走同一條路：無邊框視窗的鍵盤出口。
        var key = SourceContract.BodyOf(source, "OnPreviewKeyDown");
        Assert.Contains("Key.Escape", key);
        Assert.Contains("Close();", key);
        Assert.Contains(@"PreviewKeyDown=""OnPreviewKeyDown""", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// 表單不得存出「起訖相同」的規則：那種時段在 IsActiveAt 的閉區間語意下只在同一分鐘有效，
    /// 排程器 30 秒才跑一輪，實際上一秒都不會錄——靜靜地什麼都沒發生。
    /// </summary>
    [Fact]
    public void 起訖相同時段會被擋下而不會存成不會錄的規則()
    {
        var source = SourceContract.Read(Code);
        var build = SourceContract.BodyOf(source, "BuildFromForm");

        Assert.Contains("start == end", build);
        Assert.Contains("Schedule.SameTime", build);

        // 擋下時不得繼續存檔：回 null 由呼叫端略過 Upsert。
        Assert.Contains("return null;", build);
    }

    /// <summary>
    /// 表單預設時段必須是一整天（00:00–23:59），不是 00:00–00:00。
    /// </summary>
    /// <remarks>
    /// 預設 00:00–00:00 時，操作員只改頻道就按「新增」，存出來的正是上一條會被擋掉的
    /// 死規則；預設整天讓「快速加一條全天排程」這個最常見的操作直接正確。
    /// </remarks>
    [Fact]
    public void 表單預設時段是一整天()
    {
        var source = SourceContract.Read(Code);
        var clear = SourceContract.BodyOf(source, "ClearForm");

        Assert.Contains("StartBox.Text = \"00:00\"", clear, StringComparison.Ordinal);
        Assert.Contains("EndBox.Text = \"23:59\"", clear, StringComparison.Ordinal);

        var xaml = SourceContract.Read(Xaml);
        Assert.Contains("x:Name=\"EndBox\"", xaml, StringComparison.Ordinal);
        Assert.Matches(@"x:Name=""EndBox""[^>]*Text=""23:59""", xaml);
    }

    /// <summary>
    /// 清單的啟用／停用狀態要有顏色（StateBrush）：排程有沒有生效應該掃一眼就知道，
    /// 不必逐列讀「啟用」兩個字。
    /// </summary>
    [Fact]
    public void 清單狀態欄以顏色呈現啟停()
    {
        var source = SourceContract.Read(Code);

        // StateBrush 定義了就必須真的被畫面用到（原本只寫不讀）。
        Assert.Contains("StateBrush", source);

        var xaml = SourceContract.Read(Xaml);
        Assert.Contains(@"Foreground=""{Binding StateBrush}""", xaml, StringComparison.Ordinal);
    }
}
