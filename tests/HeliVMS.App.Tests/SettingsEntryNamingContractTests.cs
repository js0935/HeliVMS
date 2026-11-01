using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.App.Tests;

/// <summary>
/// 設定中心的入口命名契約。
/// </summary>
/// <remarks>
/// 實際事故：左側導覽頁籤（<c>Settings.NavNotify</c>）與功能整合區的按鈕
/// （<c>Main.Notification</c>）都叫「通知」，而兩者是完全不同的東西——前者是
/// <b>通道設定</b>（Webhook／SMTP／MQTT／Web Push／SNMP／team+、靜默時段），
/// 後者開的是<b>外送紀錄</b>（哪一則事件送到哪個通道、成功／失敗、重試幾次）。
/// 操作員要查「告警到底有沒有送出去」時會點進設定頁，要找設定時又點進紀錄——
/// 同名的入口等於沒有入口。現在頁籤叫「通知設定」、紀錄叫「通知紀錄」。
/// </remarks>
public sealed class SettingsEntryNamingContractTests
{
    private const string SettingsCode = "src/HeliVMS.App/SettingsWindow.xaml.cs";

    [Fact]
    public void 通知設定與通知紀錄的名稱在所有語系都不同()
    {
        foreach (var lang in I18n.Languages)
        {
            var settings = I18n.Get(lang, "Settings.NavNotify");
            var log = I18n.Get(lang, "Main.Notification");

            Assert.False(string.IsNullOrWhiteSpace(settings));
            Assert.False(string.IsNullOrWhiteSpace(log));
            Assert.NotEqual(settings, log);
        }
    }

    [Fact]
    public void 設定頁籤與紀錄入口各自接上正確的名稱鍵()
    {
        var source = SourceContract.Read(SettingsCode);

        // 左側導覽頁籤＝設定；功能整合按鈕＝紀錄。接錯就會回到「兩個入口同名」。
        Assert.Contains("NavNotify.Content = Localizer.T(\"Settings.NavNotify\")", source);
        Assert.Contains("PageNotify.Visibility", source);
        Assert.Contains("NotificationButton.Content = Localizer.T(\"Main.Notification\")", source);

        // 頁面標題與導覽一致（同一頁兩個名字會讓操作員以為走錯頁）。
        Assert.Contains("Settings.NotifyTitle", source);
    }

    [Fact]
    public void 通知紀錄的提示要指出設定在哪裡()
    {
        // 入口改名只解決一半：查紀錄的人要能立刻知道通道設定在「通知設定」頁。
        foreach (var lang in I18n.Languages)
        {
            var tip = I18n.Get(lang, "Main.NotificationTip");
            var settings = I18n.Get(lang, "Settings.NavNotify");

            Assert.Contains(settings, tip);
        }
    }
}
