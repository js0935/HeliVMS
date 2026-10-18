using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 深色主題契約：<c>App.xaml</c> 的全域 <c>Style TargetType="TextBlock"</c> 把文字設為近白
/// （<c>#EBF3FF</c>），而且這個隱性樣式<b>會滲進 ControlTemplate／ContentPresenter 產生的 TextBlock</b>。
/// 因此任何「預設模板是淺色彈出視窗」的元件（右鍵選單、工具提示）若不改成深色底，
/// 就會變成白底白字——監控中心格位右鍵選單正是如此。
/// </summary>
/// <remarks>
/// 為什麼要專門守這條：漏了不會有任何錯誤或例外，只是文字看不見，回報時也只會說
/// 「選單是白的」。此契約把「彈出元件的底色必須夠深」釘住，避免同類問題再回來。
/// </remarks>
public sealed class DarkThemeContractTests
{
    private const string AppXaml = "src/HeliVMS.App/App.xaml";

    [Theory]
    [InlineData("ContextMenu")]
    [InlineData("MenuItem")]
    [InlineData("ToolTip")]
    public void 淺色彈出元件都有深色底(string targetType)
    {
        var xaml = SourceContract.Read(AppXaml);
        var style = StyleBlock(xaml, targetType);
        Assert.NotEqual(string.Empty, style);

        var background = BackgroundOf(style);

        // MenuItem 疊在深色 ContextMenu 上，允許透明；其餘若有底色就必須夠深。
        if (background is not null)
        {
            Assert.True(IsDark(background), $"{targetType} 的背景 {background} 偏亮，與全域白色 TextBlock 相衝（白底白字）");
        }
    }

    [Fact]
    public void 右鍵選單底層必須是深色()
    {
        var xaml = SourceContract.Read(AppXaml);
        var background = BackgroundOf(StyleBlock(xaml, "ContextMenu"));
        Assert.NotNull(background);
        Assert.True(IsDark(background), $"ContextMenu 沒有深色底（{background}）");
    }

    [Fact]
    public void 右鍵選單必須覆寫模板以免預設淺色圖示欄透出()
    {
        // 實際症狀：右鍵選單「左半邊還是白色」。Aero2 預設 ContextMenu 模板會在左側硬畫
        // 一塊 #F1F1F1 的圖示欄底色與兩條分隔線；MenuItem 底為透明時就從左半邊透出來，
        // 只改 ContextMenu.Background 蓋不住。必須自訂模板把它整塊移除。
        // 這條若失敗代表有人把自訂 Template 拿掉，問題會以「選單左半邊變白」的形式回來。
        var style = StyleBlock(SourceContract.Read(AppXaml), "ContextMenu");
        Assert.Contains("<ControlTemplate TargetType=\"ContextMenu\">", style);
        Assert.DoesNotContain("#F1F1F1", style, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 下拉選單必須覆寫模板以免預設淺色彈出清單透出()
    {
        // 實際症狀：頻道管理（及所有視窗）的下拉一打開是「白底白字」。
        // Aero2 預設 ComboBox 模板的彈出清單是淺色底，在全域白色 TextBlock 之下讀不到；
        // 只設 Background/Foreground 蓋不掉，必須自訂 Template 連彈出清單一起換成深色。
        // 這條若失敗代表有人把自訂 Template 拿掉，症狀會以「下拉白底白字」的形式回來。
        var style = StyleBlock(SourceContract.Read(AppXaml), "ComboBox");
        Assert.Contains("<ControlTemplate TargetType=\"ComboBox\">", style);
        Assert.Contains("PART_Popup", style);
        Assert.Contains("#12233C", style);
    }

    [Fact]
    public void 日期彈窗的深色月曆必須從CalendarStyle入口注入()
    {
        // 實際症狀：播放視窗的日期選擇器一打開「白底白字、日期看不清」，而且不會有任何
        // 錯誤。DatePicker 的模板把 PART_Calendar.Style 綁到 DatePicker.CalendarStyle，
        // 為 null 時這個綁定會寫出 local Style，local 值會擋掉 Application.Resources 的
        // 隱含 Calendar 樣式；只剩下繼承而來的近白字（#EBF3FF）壓在主題預設的淺底上。
        // 所以只寫 <Style TargetType="Calendar"> 治不到彈窗裡那顆月曆，必須走 CalendarStyle。
        var xaml = SourceContract.Read(AppXaml);
        var datePicker = StyleBlock(xaml, "DatePicker");
        Assert.NotEqual(string.Empty, datePicker);
        Assert.Contains("Property=\"CalendarStyle\"", datePicker);
        Assert.Contains("DarkCalendar", datePicker);
    }

    [Fact]
    public void 深色月曆樣式必須把內層三種元件都接上()
    {
        // CalendarItem 的模板會把 Calendar.DayButtonStyle／CalendarButtonStyle／
        // CalendarItemStyle 綁進每一顆子按鈕的 Style；任一個漏掉，該類元件就退回主題淺底，
        // 症狀是「日標題列或日期格其中一塊變白」——一樣沒有錯誤，只是字看不見。
        var xaml = SourceContract.Read(AppXaml);
        var match = Regex.Match(
            xaml,
            "<Style x:Key=\"DarkCalendar\" TargetType=\"Calendar\">(?<body>.*?)</Style>",
            RegexOptions.Singleline);
        Assert.True(match.Success, "找不到 x:Key=\"DarkCalendar\" 的月曆樣式（彈窗月曆會退回主題淺底）");

        var body = match.Groups["body"].Value;
        Assert.Contains("CalendarItemStyle", body);
        Assert.Contains("CalendarButtonStyle", body);
        Assert.Contains("CalendarDayButtonStyle", body);
    }

    [Fact]
    public void 日期輸入框必須是深色底()
    {
        // 實際症狀：DatePicker 的文字欄是 DatePickerTextBox，不是 TextBox，吃不到全域的
        // TextBox 隱含樣式；主題給白底，字又繼承 DatePicker 的近白字 → 關掉彈窗後
        // 欄位本身同樣是白底白字。
        var style = StyleBlock(SourceContract.Read(AppXaml), "DatePickerTextBox");
        Assert.NotEqual(string.Empty, style);

        var background = BackgroundOf(style);
        Assert.NotNull(background);
        Assert.True(IsDark(background), $"DatePickerTextBox 的背景 {background} 偏亮，與近白色文字相衝（白底白字）");
    }

    private static string StyleBlock(string xaml, string targetType)
    {
        var match = Regex.Match(
            xaml,
            $"<Style TargetType=\"{Regex.Escape(targetType)}\">(?<body>.*?)</Style>",
            RegexOptions.Singleline);
        return match.Success ? match.Groups["body"].Value : string.Empty;
    }

    private static string? BackgroundOf(string style)
    {
        var match = Regex.Match(style, "<Setter Property=\"Background\" Value=\"(?<v>#[0-9A-Fa-f]{6})\"");
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static bool IsDark(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b) < 128;
    }
}
