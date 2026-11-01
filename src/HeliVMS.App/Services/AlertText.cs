using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// 事件類型的顯示名。
/// </summary>
/// <remarks>
/// 引擎寫進資料庫的是機器可讀識別碼（<c>motion</c>、<c>line_cross</c>…），直接顯示就是
/// 一串英文代號。這份對照原本只存在事件中心，通知紀錄因此把同一批代號原樣列出來——
/// 同一個事件在兩個視窗一個中文一個英文，而且新增類型時只會改到其中一邊。
/// 一律經過這裡，未列的類型原樣顯示（上游新增類型不會炸掉視窗）。
/// </remarks>
public static class EventTypeText
{
    public static string Label(string eventType) => eventType switch
    {
        "motion" => Localizer.T("EventCenter.TypeMotion"),
        "ai_person" => Localizer.T("EventCenter.TypeAiPerson"),
        "ai_vehicle" => Localizer.T("EventCenter.TypeAiVehicle"),
        "offline" => Localizer.T("EventCenter.TypeOffline"),
        "tamper" => Localizer.T("EventCenter.TypeTamper"),
        "io_input" => Localizer.T("EventCenter.TypeIoInput"),
        "line_cross" => Localizer.T("EventCenter.TypeLineCross"),
        "intrusion" => Localizer.T("EventCenter.TypeIntrusion"),
        "license_limit" => Localizer.T("EventCenter.TypeLicenseLimit"),
        "smart_alert" => Localizer.T("EventCenter.TypeSmartAlert"),
        _ => eventType,
    };
}

/// <summary>
/// 通知外送通道（<c>notification_log.route</c>）的顯示名。
/// </summary>
/// <remarks>
/// 資料庫存的是通道代號，多個通道以 <c>+</c> 串接（例：<c>webhook+smtp</c>）。
/// 逐段翻譯再以在地化分隔符重組，才不會出現「已通知（webhook+smtp）」這種半英文訊息。
/// 通訊協定名（Webhook／SMTP／MQTT／SNMP）保留原文——那是維運人員用來對照設備設定的名字；
/// 而 push 與 teamplus 這種看不出意思的，直接寫成「瀏覽器推播」「team+ 訊息」。
/// </remarks>
public static class NotificationRouteText
{
    public static string Label(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return string.Empty;
        }

        var parts = route.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return route;
        }

        var labels = parts.Select(p => p switch
        {
            "webhook" => Localizer.T("NotifyLog.RouteWebhook"),
            "smtp" => Localizer.T("NotifyLog.RouteSmtp"),
            "mqtt" => Localizer.T("NotifyLog.RouteMqtt"),
            "push" => Localizer.T("NotifyLog.RoutePush"),
            "snmp" => Localizer.T("NotifyLog.RouteSnmp"),
            "teamplus" => Localizer.T("NotifyLog.RouteTeamPlus"),
            _ => p,
        });

        return string.Join(Localizer.T("NotifyLog.RouteSep"), labels);
    }
}
