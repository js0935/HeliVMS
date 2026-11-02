using System.Globalization;

namespace HeliVMS.Alarms;

/// <summary>規則的嚴重度（寫進通知的 Detail 前綴，事件中心照文字顯示）。</summary>
public static class RuleSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";

    /// <summary>全部合法值（規則編輯器的下拉來源）。</summary>
    public static IReadOnlyList<string> All { get; } = [Info, Warning, Critical];
}

/// <summary>
/// 「簡易規則表單 → 條件 JSON」的組裝（純函式）。
/// </summary>
/// <remarks>
/// <para>
/// 規則的邏輯條件全部走 <see cref="RuleExpressionNode"/>（JSON DSL）。之前的編輯器要操作員
/// 手寫 JSON——等於只有工程師能建規則。這個組裝器把常見形狀收斂成四個維度：
/// 事件比對（類型＋頻道）、計數窗（N 次／T 秒）、先前窗（T 秒內發生過）、時段（HHMM–HHMM）。
/// </para>
/// <para>
/// 組合順序是確定的（時段 → 先前窗 → 主條件），同形狀的規則會產生同一份 JSON，
/// 迴歸才比得出來；單一條件時不包 <c>all</c>，讓 JSON 跟人想的結構一致。
/// </para>
/// </remarks>
public static class RuleExpressionBuilder
{
    /// <summary>組出條件 JSON；沒有任何條件時回 null（空的規則不該存）。</summary>
    public static string? Build(
        string eventType,
        int? channelId = null,
        int? countAtLeast = null,
        int? countSeconds = null,
        int? timeStartHhmm = null,
        int? timeEndHhmm = null)
    {
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return null;
        }

        var conditions = new List<RuleExpressionNode>();

        if (timeStartHhmm is int start && timeEndHhmm is int end)
        {
            conditions.Add(new RuleExpressionNode
            {
                TimeBetween = new RuleTimeBetweenPredicate { StartHhmm = start, EndHhmm = end },
            });
        }

        if (countAtLeast is int atLeast && atLeast > 0)
        {
            conditions.Add(new RuleExpressionNode
            {
                Count = new RuleCountPredicate
                {
                    AtLeast = atLeast,
                    Seconds = countSeconds ?? 60,
                    Match = new RuleEventPredicate { EventType = eventType, ChannelId = channelId },
                },
            });
        }
        else
        {
            conditions.Add(new RuleExpressionNode
            {
                Match = new RuleEventPredicate { EventType = eventType, ChannelId = channelId },
            });
        }

        var expression = conditions.Count == 1
            ? conditions[0]
            : new RuleExpressionNode { All = conditions };

        return expression.ToJson();
    }

    /// <summary>組出動作 JSON（severity／tag／notify；全空時回空字串＝不變更）。</summary>
    /// <remarks>
    /// <c>notify=false</c> 必須真的寫進 JSON：它代表「操作員明確關閉通知」。
    /// 早期把 false 寫成 null（被 <see cref="RuleJson.Options"/> 的 WhenWritingNull 省略），
    /// 執行期讀不到就套預設值（要通知）——取消勾選被靜默忽略，通知照發，日誌乾淨無聲。
    /// </remarks>
    public static string BuildActions(string? severity, string? tag, bool notify)
    {
        var actions = new RuleActions(
            Severity: NormalizeSeverity(severity),
            Tag: string.IsNullOrWhiteSpace(tag) ? null : tag.Trim(),
            Notify: notify ? true : false);

        // 全預設 → 空字串：ActionsJson 為空代表「不變更」，讀回來就是預設值。
        return actions.Severity is null && actions.Tag is null && actions.Notify is not false
            ? string.Empty
            : actions.ToJson();
    }

    /// <summary>嚴重度正規化：未列的值視為未指定（避免把亂字串寫進通知）。</summary>
    public static string? NormalizeSeverity(string? severity) =>
        severity is not null && RuleSeverity.All.Contains(severity.Trim(), StringComparer.OrdinalIgnoreCase)
            ? severity.Trim().ToLowerInvariant()
            : null;

    /// <summary>簡易表單的一組值（<see cref="TryParseExpression"/> 的回傳）。</summary>
    public sealed record RuleFormValues(
        string EventType,
        int? ChannelId,
        int? CountAtLeast,
        int? CountSeconds,
        int? TimeStartHhmm,
        int? TimeEndHhmm);

    /// <summary>
    /// 把條件 JSON 解回簡易表單的值；形狀不是本組裝器會產生的就回 null（編輯器退回進階模式）。
    /// </summary>
    /// <remarks>
    /// 只認得 <see cref="Build"/> 會產生的三種形狀：單一 match、單一 count、all[timeBetween, 條件]。
    /// 其餘（any／not／within／巢狀組合）是進階模式的手寫 JSON，硬解回表單會把規則改壞——
    /// 寧可讓操作員在進階模式裡看到原始 JSON。
    /// </remarks>
    public static RuleFormValues? TryParseExpression(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        RuleExpressionNode? node;
        try
        {
            node = System.Text.Json.JsonSerializer.Deserialize<RuleExpressionNode>(json, RuleJson.Options);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (node is null)
        {
            return null;
        }

        // 形狀一：all[timeBetween, 主條件]。
        if (node.All is { } list && list.Count == 2
            && list[0].TimeBetween is { } between && list[0].Match is null && list[0].Count is null)
        {
            var inner = ParseLeaf(list[1]);
            if (inner is null)
            {
                return null;
            }

            return new RuleFormValues(
                inner.EventType, inner.ChannelId, inner.CountAtLeast, inner.CountSeconds,
                between.StartHhmm, between.EndHhmm);
        }

        // 形狀二／三：單一 match 或 count（同一個葉節點解法）。
        return ParseLeaf(node);
    }

    /// <summary>把葉節點解回表單值；不是單一 match/count 就回 null。</summary>
    private static RuleFormValues? ParseLeaf(RuleExpressionNode node)
    {
        if (node.Match is { } match && !string.IsNullOrWhiteSpace(match.EventType)
            && node.Count is null && node.TimeBetween is null && node.All is null)
        {
            return new RuleFormValues(match.EventType!, match.ChannelId, null, null, null, null);
        }

        if (node.Count is { } count && !string.IsNullOrWhiteSpace(count.Match?.EventType)
            && node.Match is null && node.TimeBetween is null && node.All is null)
        {
            return new RuleFormValues(count.Match.EventType!, count.Match.ChannelId, count.AtLeast, count.Seconds, null, null);
        }

        return null;
    }

    /// <summary>動作 JSON 解回表單值（severity／tag／notify）；空字串或解析失敗＝預設值。</summary>
    public static (string? Severity, string? Tag, bool Notify) TryParseActions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null, true);
        }

        try
        {
            var actions = System.Text.Json.JsonSerializer.Deserialize<RuleActions>(json, RuleJson.Options);
            return (actions?.Severity, actions?.Tag, actions?.Notify ?? true);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null, true);
        }
    }

    /// <summary>把「人看的條件」摘成一句（規則清單與通知 Detail 用）。</summary>
    public static string Describe(string eventType, int? channelId, int? countAtLeast, int? countSeconds)
    {
        var text = eventType;
        if (channelId is int channel)
        {
            text += $" @ch{channel}";
        }

        if (countAtLeast is int atLeast && atLeast > 1)
        {
            text += string.Format(
                CultureInfo.InvariantCulture,
                " x{0}/{1}s",
                atLeast,
                countSeconds ?? 60);
        }

        return text;
    }
}
