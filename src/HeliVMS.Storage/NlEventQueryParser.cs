using System.Text.RegularExpressions;

namespace HeliVMS.Storage;

/// <summary>結構化事件查詢（M107，§14.7 #7 NLP 查詢解析）：供 <see cref="AlarmEventRepository.QueryArgs"/> 轉用。</summary>
public sealed record ParsedEventQuery(
    int? ChannelId,
    IReadOnlySet<string> EventTypes,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int? Limit,
    IReadOnlyList<string> Unmatched)
{
    public static ParsedEventQuery Empty(IReadOnlyList<string> unmatched) =>
        new(null, new HashSet<string>(), null, null, null, unmatched);

    /// <summary>轉換為 <see cref="AlarmEventRepository.QueryArgs"/>；事件類型多個時只取最小 lexicographic（呼叫端可另群組）。</summary>
    public AlarmEventRepository.QueryArgs ToQueryArgs()
    {
        return new AlarmEventRepository.QueryArgs
        {
            ChannelId = ChannelId,
            EventType = EventTypes.OrderBy(e => e).FirstOrDefault(),
            FromUtc = FromUtc ?? DateTime.MinValue,
            ToUtc = ToUtc ?? DateTime.MaxValue,
            Limit = Limit ?? EventSearchRepository.DefaultLimit,
        };
    }
}

/// <summary>
/// 中文自然語言事件查詢解析器（M107，§14.7 #7，純 BCL，規則式 NLP L0）：
/// 把「5 頻道 昨天 凌晨 移動 最新 20 筆」解析為結構化 {ChannelId, EventTypes, From/To, Limit,
/// Unmatched}。支援：頻道（×頻道/路/號 前後置＋全形數字）、時間（今天/昨天/最近 N 分鐘/小時/
/// 凌晨-上午-下午-晚上/HH:MM）、事件類型關鍵字表（motion/ai_intrusion/line_cross/dwell/
/// left_object/removed_object/face/lpr/crowd/offline…）、「最新 N 筆/條」上限；
/// 未辨識詞（剔除連接詞後）收進 Unmatched 供介面提示「未解析：…」。
/// </summary>
public static class NlEventQueryParser
{
    private static readonly IReadOnlyList<(string Keyword, string EventType)> TypeKeywords =
    [
        ("移動", "motion"),
        ("動態", "motion"),
        ("入侵", "ai_intrusion"),
        ("越線", "line_cross"),
        ("跨越", "line_cross"),
        ("徘徊", "dwell"),
        ("逗留", "loitering"),
        ("群聚", "crowd"),
        ("人群", "crowd"),
        ("遺留", "left_object"),
        ("遺棄", "left_object"),
        ("移除", "removed_object"),
        ("人臉", "face"),
        ("車牌", "lpr"),
        ("離線", "offline"),
        ("上線", "online"),
    ];

    private static readonly string[] Connectors =
    [
        "的", "於", "在", "裡", "内", "前", "之前", "以前", "以後", "後", "時", "所", "有", "查", "找",
        "請", "顯示", "報告", "記錄", "事件", "內容", "全部", "任何", "所有", "頻道", "路", "號", "筆",
        "條", "最新", "最多", "凌晨", "早上", "上午", "下午", "晚上",
    ];

    private static readonly string[] MathConnectors = ["分鐘", "小時"];

    public static ParsedEventQuery Parse(string query, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ParsedEventQuery.Empty([]);
        }

        string work = query;
        int? channelId = null;
        var eventTypes = new HashSet<string>(StringComparer.Ordinal);
        DateTime? fromUtc = null;
        DateTime? toUtc = null;
        int? limit = null;
        var day = nowUtc.Date;

        // 頻道：數字(全形/半形)＋ 頻道/路/號，前後置皆可。
        var channel = Regex.Match(work, @"(?:頻道|路|號)\s*(\d{1,4})|(\d{1,4})\s*(?:頻道|路|號)");
        if (channel.Success)
        {
            var digits = channel.Groups[1].Success ? channel.Groups[1].Value : channel.Groups[2].Value;
            channelId = NormalizeDigits(digits);
            work = RemoveAt(work, channel.Index, channel.Length);
        }

        var recently = Regex.Match(work, @"最近\s*([0-9０-９]+)\s*(分鐘|小時)");
        if (recently.Success)
        {
            var amount = NormalizeDigits(recently.Groups[1].Value);
            fromUtc = nowUtc.Add(
                recently.Groups[2].Value == "小時" ? TimeSpan.FromHours(-amount) : TimeSpan.FromMinutes(-amount));
            work = RemoveAt(work, recently.Index, recently.Length);
        }

        // 昨天/今天；時段詞若存在會以相同基準日套用。
        var periodBase = day;
        var dayMatch = Regex.Match(work, @"今天|昨天");
        if (dayMatch.Success)
        {
            if (dayMatch.Value == "昨天")
            {
                fromUtc = day.AddDays(-1);
                toUtc = day;
                periodBase = day.AddDays(-1);
            }
            else
            {
                fromUtc = day;
                toUtc = nowUtc;
            }

            work = RemoveAt(work, dayMatch.Index, dayMatch.Length);
        }

        // 時段詞（錨定在 now 所在日；若有「昨天」則錨定昨天）。
        var period = Regex.Match(work, @"凌晨|早上|上午|下午|晚上");
        if (period.Success)
        {
            (var pFrom, var pTo) = period.Value switch
            {
                "凌晨" => (TimeSpan.Zero, TimeSpan.FromHours(6)),
                "早上" or "上午" => (TimeSpan.FromHours(6), TimeSpan.FromHours(12)),
                "下午" => (TimeSpan.FromHours(12), TimeSpan.FromHours(18)),
                _ => (TimeSpan.FromHours(18), TimeSpan.FromHours(24)),
            };
            fromUtc = periodBase + pFrom;
            toUtc = periodBase + pTo;
            work = RemoveAt(work, period.Index, period.Length);
        }

        // HH:MM（半形/全形冒號）。
        var clock = Regex.Match(work, @"(\d{1,2})\s*[:：]\s*(\d{2})");
        if (clock.Success)
        {
            var at = day.AddHours(NormalizeDigits(clock.Groups[1].Value))
                .AddMinutes(NormalizeDigits(clock.Groups[2].Value));
            fromUtc = at;
            toUtc = at.AddMinutes(10);
            work = RemoveAt(work, clock.Index, clock.Length);
        }

        // 最新/最多 N 筆/條。
        var cap = Regex.Match(work, @"(?:最新|最多|\d{1,4})\s*([0-9０-９]{1,4})\s*(?:筆|條)");
        if (cap.Success)
        {
            limit = NormalizeDigits(cap.Groups[1].Value);
            work = RemoveAt(work, cap.Index, cap.Length);
        }

        // 事件類型關鍵字（多個皆收）。
        foreach (var (keyword, eventType) in TypeKeywords)
        {
            if (work.Contains(keyword, StringComparison.Ordinal))
            {
                eventTypes.Add(eventType);
            }
        }

        foreach (var keyword in TypeKeywords.Select(t => t.Keyword))
        {
            work = work.Replace(keyword, " ", StringComparison.Ordinal);
        }

        foreach (var connector in Connectors)
        {
            work = work.Replace(connector, " ", StringComparison.Ordinal);
        }

        foreach (var connector in MathConnectors)
        {
            _ = connector;
        }

        var unmatched = Regex.Split(work, @"\s+")
            .Where(s => s.Length > 0)
            .ToList();

        return new ParsedEventQuery(channelId, eventTypes, fromUtc, toUtc, limit, unmatched);
    }

    private static int NormalizeDigits(string value) => int.Parse(ToAscii(value));

    private static string ToAscii(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '０' and <= '９')
            {
                chars[i] = (char)('0' + (chars[i] - '０'));
            }
        }

        return new string(chars);
    }

    private static string RemoveAt(string input, int index, int length)
        => input.Remove(index, length);
}