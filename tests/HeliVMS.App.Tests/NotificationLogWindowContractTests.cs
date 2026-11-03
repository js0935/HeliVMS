using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.App.Tests;

/// <summary>
/// 通知紀錄（<c>NotificationLogWindow</c>）的版面與中文化契約。
/// </summary>
/// <remarks>
/// 這個視窗原本就是無邊框最大化，但<b>沒有任何關閉控制</b>（沒有標題列、沒有 ✕、沒有 Esc），
/// 唯一離開方式是 Alt+F4；而且事件類型與外送通道直接顯示資料庫裡的機器代號
/// （<c>motion</c>／<c>webhook+smtp</c>），同一則事件在事件中心是中文、在這裡是英文。
/// </remarks>
public sealed class NotificationLogWindowContractTests
{
    private const string Code = "src/HeliVMS.App/NotificationLogWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/NotificationLogWindow.xaml";

    /// <summary>
    /// 離開路徑：工具列的「關閉」鈕與 Esc（操作員指定不加表頭列，所以沒有 ✕）。
    /// </summary>
    /// <remarks>
    /// 這個視窗原本連一個關閉控制都沒有（無邊框最大化、唯一離開方式是 Alt+F4）。
    /// 操作員看過加了表頭（標題＋✕）的版本後指定：不要表頭，只要工具列那顆「關閉」——
    /// 所以表頭列不得再長回來，否則就是沒問過又加回去。
    /// </remarks>
    [Fact]
    public void 通知紀錄以工具列關閉鈕與Esc離開且沒有表頭列()
    {
        var xaml = SourceContract.Read(Xaml);
        var source = SourceContract.Read(Code);

        Assert.Contains("WindowState=\"Maximized\"", xaml, StringComparison.Ordinal);

        // 保留：工具列右側的關閉鈕已統一為紅 X（IsCancel，Esc 也會觸發）與 Esc。
        Assert.Matches(
            @"<Button[^>]*x:Name=""CloseButton""[^>]*Content=""✕""",
            xaml);
        Assert.Contains("Close()", SourceContract.BodyOf(source, "OnCloseClicked"));
        Assert.Matches(
            @"<Button[^>]*x:Name=""CloseButton""[^>]*IsCancel=""True""",
            xaml);

        var key = SourceContract.BodyOf(source, "OnPreviewKeyDown");
        Assert.Contains("Key.Escape", key);
        Assert.Contains("Close()", key);
        Assert.Contains(@"PreviewKeyDown=""OnPreviewKeyDown""", xaml, StringComparison.Ordinal);

        // 取消：表頭列（視窗標題＋右上角 ✕）。
        Assert.DoesNotContain("HeaderTitle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderCloseButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderTitle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HeaderCloseButton", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// 事件類型與外送通道要顯示在地化名稱，而且與事件中心共用同一份對照。
    /// </summary>
    /// <remarks>
    /// 兩份對照一定會漂移：新增事件類型時只改到其中一邊，另一邊就退回顯示英文代號。
    /// 所以事件中心也不得再自己寫一份 switch。
    /// </remarks>
    [Fact]
    public void 事件類型與外送通道顯示在地化名稱()
    {
        var source = SourceContract.Read(Code);

        Assert.Contains("EventTypeText.Label(", source);
        Assert.Contains("NotificationRouteText.Label(", source);
        Assert.DoesNotContain("\"motion\" =>", source);

        // 綁定要指向新的顯示屬性，不是原始代號欄位。
        var xaml = SourceContract.Read(Xaml);
        Assert.Contains("{Binding EventTypeLabel}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding RouteLabel}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding EventType}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding Route}", xaml, StringComparison.Ordinal);

        // 事件中心改用同一份對照（唯一來源）。
        var center = SourceContract.Read("src/HeliVMS.App/EventCenterWindow.xaml.cs");
        Assert.Contains("EventTypeText.Label(EventType)", center);
        Assert.DoesNotContain("\"line_cross\" =>", center);
    }

    /// <summary>
    /// 共用對照用到的每個字串鍵都必須具備三語——它不在 <c>WpfI18nContractTests</c>
    /// 的視窗清單裡，少了這道棘輪就會出現「鍵存在但某語系是空的」。
    /// </summary>
    [Fact]
    public void 共用對照的字串鍵具備三語()
    {
        var helper = SourceContract.Read("src/HeliVMS.App/Services/AlertText.cs");

        var keys = new[]
        {
            "EventCenter.TypeMotion", "EventCenter.TypeAiPerson", "EventCenter.TypeAiVehicle",
            "EventCenter.TypeOffline", "EventCenter.TypeTamper", "EventCenter.TypeIoInput",
            "EventCenter.TypeLineCross", "EventCenter.TypeIntrusion", "EventCenter.TypeLicenseLimit",
            "EventCenter.TypeSmartAlert",
            "NotifyLog.RouteWebhook", "NotifyLog.RouteSmtp", "NotifyLog.RouteMqtt",
            "NotifyLog.RoutePush", "NotifyLog.RouteSnmp", "NotifyLog.RouteTeamPlus",
            "NotifyLog.RouteSep",
        };

        // 先確認對照表真的引用這些鍵（否則「三語齊備」是在檢查沒人用的字串）。
        foreach (var key in keys)
        {
            Assert.Contains($"\"{key}\"", helper, StringComparison.Ordinal);
            foreach (var lang in I18n.Languages)
            {
                Assert.NotEqual(key, I18n.Get(lang, key));
            }
        }
    }
}
