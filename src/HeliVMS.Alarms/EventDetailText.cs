using System.Globalization;
using System.Text.RegularExpressions;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 事件中心「詳情」欄的在地化顯示：把事件引擎寫入的機器可讀 detail（例如
/// <c>person conf=0.92 bbox=(...)</c>）轉成使用者看得懂的語言。
///
/// <para>
/// 儲存層的格式刻意不動：<see cref="DetectionDetail.TryParse"/> 與快照遮蔽靠它解析，
/// 改了會讓遮蔽畫面「無框可拉」；因此只在顯示時轉換，資料庫內容與解析器完全不受影響。
/// </para>
///
/// <para>
/// 認不出的格式原樣回傳（含分析模組已是中文的 detail），上游新增事件類型不會讓視窗顯示空白。
/// </para>
/// </summary>
public static class EventDetailText
{
    /// <summary>把單筆事件的 detail 轉成 <paramref name="lang"/> 語言的顯示字串。</summary>
    public static string Format(string lang, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return I18n.Get(lang, "EventCenter.DetailNone");
        }

        if (DetectionDetail.TryParse(detail, out var det))
        {
            return Build(
                lang,
                "EventCenter.DetailAi",
                ClassLabel(lang, det.Class),
                det.Confidence);
        }

        var motion = Regex.Match(detail, @"^duration=(?<d>\d+)ms peak=(?<p>\d+)%$");
        if (motion.Success)
        {
            return Build(lang, "EventCenter.DetailMotion", motion.Groups["d"].Value, motion.Groups["p"].Value);
        }

        var audio = Regex.Match(
            detail,
            @"^kind=(?<k>[a-z_]+);peak_db=(?<pk>-?[0-9.]+);mean_db=(?<mn>-?[0-9.]+);duration=(?<d>\d+)ms$");
        if (audio.Success)
        {
            return Build(
                lang,
                "EventCenter.DetailAudio",
                AudioKindLabel(lang, audio.Groups["k"].Value),
                audio.Groups["pk"].Value,
                audio.Groups["mn"].Value,
                audio.Groups["d"].Value);
        }

        var tamper = Regex.Match(
            detail,
            @"^kind=(?<k>[a-z_]+);brightness=(?<b>\d+);edge=(?<e>\d+);duration=(?<d>\d+)ms$");
        if (tamper.Success)
        {
            return Build(
                lang,
                "EventCenter.DetailTamper",
                TamperKindLabel(lang, tamper.Groups["k"].Value),
                tamper.Groups["b"].Value,
                tamper.Groups["e"].Value,
                tamper.Groups["d"].Value);
        }

        var io = Regex.Match(
            detail,
            @"^device=(?<dev>.*?);io=(?<io>.*?);index=(?<idx>\d+);priority=(?<pri>.*?);state=(?<st>\w+)$");
        if (io.Success)
        {
            return Build(
                lang,
                "EventCenter.DetailIo",
                io.Groups["dev"].Value,
                io.Groups["io"].Value,
                io.Groups["idx"].Value,
                io.Groups["pri"].Value);
        }

        if (string.Equals(detail, "connection lost", StringComparison.Ordinal))
        {
            return I18n.Get(lang, "EventCenter.DetailOfflineLost");
        }

        var offlineDur = Regex.Match(detail, @"^offline_duration=(?<d>\d{2}:\d{2}:\d{2})$");
        if (offlineDur.Success)
        {
            return Build(lang, "EventCenter.DetailOfflineDuration", offlineDur.Groups["d"].Value);
        }

        return detail;
    }

    /// <summary>偵測類別代碼（person/car…）的顯示名；未列者原樣回傳。</summary>
    public static string ClassLabel(string lang, string cls) => cls switch
    {
        "person" => I18n.Get(lang, "EventCenter.ClassPerson"),
        "car" => I18n.Get(lang, "EventCenter.ClassCar"),
        "bus" => I18n.Get(lang, "EventCenter.ClassBus"),
        "truck" => I18n.Get(lang, "EventCenter.ClassTruck"),
        "motorcycle" => I18n.Get(lang, "EventCenter.ClassMotorcycle"),
        "bicycle" => I18n.Get(lang, "EventCenter.ClassBicycle"),
        "dog" => I18n.Get(lang, "EventCenter.ClassDog"),
        "cat" => I18n.Get(lang, "EventCenter.ClassCat"),
        _ => cls,
    };

    private static string AudioKindLabel(string lang, string kind) => kind switch
    {
        "burst" => I18n.Get(lang, "EventCenter.KindBurst"),
        "sustained" => I18n.Get(lang, "EventCenter.KindSustained"),
        "silence" => I18n.Get(lang, "EventCenter.KindSilence"),
        _ => kind,
    };

    private static string TamperKindLabel(string lang, string kind) => kind switch
    {
        "blackout" => I18n.Get(lang, "EventCenter.KindBlackout"),
        "whiteout" => I18n.Get(lang, "EventCenter.KindWhiteout"),
        "covered" => I18n.Get(lang, "EventCenter.KindCovered"),
        _ => kind,
    };

    /// <summary>
    /// 取翻譯後格式化（三語鍵由 WpfI18nContractTests 的棘輪釘住，正常不會缺）。
    /// 數字一律用 <see cref="CultureInfo.InvariantCulture"/>，小數點不隨系統地區變動。
    /// </summary>
    private static string Build(string lang, string key, params object[] args)
        => string.Format(CultureInfo.InvariantCulture, I18n.Get(lang, key), args);
}
