using System.Text.Json;
using System.Text.Json.Serialization;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>規則觸發後行為（M62；actions_json，可選，缺省即不變更）。</summary>
public sealed record RuleActions(string? Severity = null, string? Tag = null, bool? Notify = null)
{
    public string ToJson() => JsonSerializer.Serialize(this, RuleJson.Options);

    internal static RuleActions FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new RuleActions();
        }

        try
        {
            return JsonSerializer.Deserialize<RuleActions>(json, RuleJson.Options) ?? new RuleActions();
        }
        catch (JsonException)
        {
            return new RuleActions();
        }
    }
}

/// <summary>單一事件比對（葉節點：eventType／channelId）。</summary>
public sealed class RuleEventPredicate
{
    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("channelId")]
    public int? ChannelId { get; set; }
}

/// <summary>「先前窗內」：於最近 seconds 秒內發生過符合 match 之事件（不含目前事件本身）。</summary>
public sealed class RuleWindowPredicate
{
    [JsonPropertyName("seconds")]
    public int Seconds { get; set; }

    [JsonPropertyName("match")]
    public RuleEventPredicate? Match { get; set; }
}

/// <summary>「計數窗」：最近 seconds 秒內（含目前事件）符合 match 之事件數 ≥ atLeast。</summary>
public sealed class RuleCountPredicate
{
    [JsonPropertyName("atLeast")]
    public int AtLeast { get; set; }

    [JsonPropertyName("seconds")]
    public int Seconds { get; set; }

    [JsonPropertyName("match")]
    public RuleEventPredicate? Match { get; set; }
}

/// <summary>時段（本機時區當地時間之 HHMM，跨午夜會包夜）：[start, end)。</summary>
public sealed class RuleTimeBetweenPredicate
{
    [JsonPropertyName("start")]
    public int StartHhmm { get; set; }

    [JsonPropertyName("end")]
    public int EndHhmm { get; set; }
}

/// <summary>規則條件樹（M62，§5.10）：葉 match、組合 all／any／not、窗 within／count、時段 timeBetween。</summary>
public sealed class RuleExpressionNode
{
    [JsonPropertyName("match")]
    public RuleEventPredicate? Match { get; set; }

    [JsonPropertyName("all")]
    public List<RuleExpressionNode>? All { get; set; }

    [JsonPropertyName("any")]
    public List<RuleExpressionNode>? Any { get; set; }

    [JsonPropertyName("not")]
    public RuleExpressionNode? Not { get; set; }

    [JsonPropertyName("within")]
    public RuleWindowPredicate? Within { get; set; }

    [JsonPropertyName("count")]
    public RuleCountPredicate? Count { get; set; }

    [JsonPropertyName("timeBetween")]
    public RuleTimeBetweenPredicate? TimeBetween { get; set; }

    public static RuleExpressionNode? Parse(string json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<RuleExpressionNode>(json, RuleJson.Options);

    public string ToJson() => JsonSerializer.Serialize(this, RuleJson.Options);
}

/// <summary>規則命中結果（M62）。</summary>
public sealed record RuleMatch(int RuleId, string Name, RuleActions Actions);

internal static class RuleJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// 複合事件規則求值器（M62，§5.10）：自啟用規則（ai_rules 設定層）建構，逐事件 <see cref="Feed"/>，
/// 以滑動窗（<c>_buffer</c>）支援 within／count 窗條件。純邏輯、離線可測。
/// </summary>
public sealed class RuleEvaluator
{
    private readonly List<(AiRuleRecord Rule, RuleExpressionNode Expr)> _rules = new();
    private readonly List<AlarmEventRecord> _buffer = new();
    private readonly TimeSpan _bufferSpan;

    public RuleEvaluator(IEnumerable<AiRuleRecord> enabledRules, TimeSpan? bufferSpan = null)
    {
        _bufferSpan = bufferSpan ?? TimeSpan.FromHours(2);
        if (enabledRules is null)
        {
            return;
        }

        foreach (var rule in enabledRules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            try
            {
                var expr = RuleExpressionNode.Parse(rule.ExpressionJson);
                if (expr is not null)
                {
                    _rules.Add((rule, expr));
                }
            }
            catch (JsonException)
            {
                // 規則 JSON 損壞則略過該規則，不影響其他規則
            }
        }
    }

    /// <summary>目前緩衝事件數（供測試／診斷）。</summary>
    public int BufferCount => _buffer.Count;

    public void Reset() => _buffer.Clear();

    public IReadOnlyList<RuleMatch> Feed(AlarmEventRecord evt)
    {
        Prune(evt.StartUtc);
        var matches = new List<RuleMatch>();
        foreach (var (rule, expr) in _rules)
        {
            if (Matches(expr, evt))
            {
                matches.Add(new RuleMatch(
                    rule.Id,
                    rule.Name,
                    RuleActions.FromJson(rule.ActionsJson)));
            }
        }

        _buffer.Add(evt);
        return matches;
    }

    private void Prune(DateTime now)
    {
        var cutoff = now - _bufferSpan;
        _buffer.RemoveAll(e => e.StartUtc < cutoff);
    }

    private bool Matches(RuleExpressionNode node, AlarmEventRecord evt)
    {
        if (node.Match is { } m)
        {
            return MatchEvent(m, evt);
        }

        if (node.All is { Count: > 0 } all)
        {
            return all.All(x => Matches(x, evt));
        }

        if (node.Any is { Count: > 0 } any)
        {
            return any.Any(x => Matches(x, evt));
        }

        if (node.Not is { } not)
        {
            return !Matches(not, evt);
        }

        if (node.Within is { } w)
        {
            return WindowHit(w, evt);
        }

        if (node.Count is { } c)
        {
            return CountHit(c, evt);
        }

        if (node.TimeBetween is { } tb)
        {
            return TimeBetweenHit(tb, evt);
        }

        return false;
    }

    private static bool MatchEvent(RuleEventPredicate p, AlarmEventRecord evt)
    {
        if (p.EventType is { } et
            && !string.Equals(et, evt.EventType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (p.ChannelId is { } c && c != evt.ChannelId)
        {
            return false;
        }

        return true;
    }

    private bool WindowHit(RuleWindowPredicate w, AlarmEventRecord evt)
    {
        if (w.Match is null)
        {
            return false;
        }

        var cutoff = evt.StartUtc - TimeSpan.FromSeconds(Math.Max(0, w.Seconds));
        return _buffer.Any(e => !ReferenceEquals(e, evt) && e.StartUtc >= cutoff && MatchEvent(w.Match, e));
    }

    private bool CountHit(RuleCountPredicate c, AlarmEventRecord evt)
    {
        if (c.Match is null)
        {
            return false;
        }

        var cutoff = evt.StartUtc - TimeSpan.FromSeconds(Math.Max(0, c.Seconds));
        var count = _buffer
            .Where(e => e.StartUtc >= cutoff && MatchEvent(c.Match, e))
            .Concat(new[] { evt }.Where(e => MatchEvent(c.Match, e)))
            .Count();
        return count >= Math.Max(1, c.AtLeast);
    }

    private static bool TimeBetweenHit(RuleTimeBetweenPredicate tb, AlarmEventRecord evt)
    {
        var local = evt.StartUtc.Kind == DateTimeKind.Unspecified
            ? evt.StartUtc
            : evt.StartUtc.ToLocalTime();
        var hhmm = (local.Hour * 100) + local.Minute;
        var start = tb.StartHhmm;
        var end = tb.EndHhmm;
        if (start == end)
        {
            return true;
        }

        return start < end
            ? hhmm >= start && hhmm < end
            : hhmm >= start || hhmm < end;
    }
}