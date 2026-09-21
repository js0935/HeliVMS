namespace HeliVMS.Storage;

/// <summary>影像串流模式（M76，§15.2 雙碼流）：主/次流；事件熱度上升切主流，頻寬或無觀看需求下降切次流。</summary>
public enum StreamKind
{
    /// <summary>主碼流（高解析）。</summary>
    Main,

    /// <summary>次碼流（低碼率）。</summary>
    Sub,
}

/// <summary>切流決策（M76）：目標與理由；「立即」由調度層依頻道實際可用性採納。</summary>
public sealed record StreamSwitchDecision(StreamKind Target, string? Reason)
{
    /// <summary>維持現狀（Target＝Current）。</summary>
    public bool NoChange => Reason is null;
}

/// <summary>
/// 雙碼流 Smart 切流決策引擎（M76，§15.2，純 BCL、時鐘注入）：
/// 每頻道以「切後維持期（honeymoon）」抑制抖動，再依 ①事件熱度（事件窗口內加權分≥臨界）
/// ②頻寬水位（全系統主流總碼率≥上限）③觀看需求（0 名收看且為主流→省流）給出建議。
/// 決策為建議（Target/Reason），由調度層套用並回報 <see cref="ApplySwitch"/> 更新狀態。
/// </summary>
public sealed class StreamSwitcher
{
    public const int DefaultMinHoldSec = 30;
    public const int DefaultEventWindowSec = 30;

    private readonly int _minHoldSec;
    private readonly int _eventWindowSec;
    private readonly IReadOnlyDictionary<string, int> _eventWeights;
    private readonly long _mainBandwidthLimitBytesPerSec;

    private readonly Dictionary<long, StreamKind> _current = new();
    private readonly Dictionary<long, DateTime> _lastSwitch = new();

    public StreamSwitcher(
        int minHoldSec = DefaultMinHoldSec,
        int eventWindowSec = DefaultEventWindowSec,
        IReadOnlyDictionary<string, int>? eventWeights = null,
        long mainBandwidthLimitBytesPerSec = 60_000_000)
    {
        if (minHoldSec < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minHoldSec), "維持期不可為負。");
        }

        if (eventWindowSec <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventWindowSec), "事件窗口須為正。");
        }

        if (mainBandwidthLimitBytesPerSec <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mainBandwidthLimitBytesPerSec), "主流頻寬上限須為正。");
        }

        _minHoldSec = minHoldSec;
        _eventWindowSec = eventWindowSec;
        _eventWeights = eventWeights ?? new Dictionary<string, int>
        {
            ["ai_intrusion"] = 100,
            ["tamper"] = 90,
            ["ai_line_cross"] = 60,
            ["motion"] = 40,
        };
        _mainBandwidthLimitBytesPerSec = mainBandwidthLimitBytesPerSec;
    }

    /// <summary>頻道目前串流模式；尚未設定→Main（預設主碼流）。</summary>
    public StreamKind GetCurrent(long channelId) => _current.GetValueOrDefault(channelId, StreamKind.Main);

    /// <summary>上次切換時間；從未切換→null。</summary>
    public DateTime? GetLastSwitch(long channelId) => _lastSwitch.TryGetValue(channelId, out var t) ? t : null;

    /// <summary>
    /// 評価一次切流建議。事件串列為「近《eventWindowSec》秒內事件」，時間戳用於窗口；呼叫前不排序。
    /// 找不到頻道 id 僅視為新頻道（預設 Main）。
    /// </summary>
    public StreamSwitchDecision Evaluate(
        long channelId,
        long currentMainBytesPerSec,
        IReadOnlyList<(string EventType, DateTime AtUtc)> recentEvents,
        int activeViewers,
        DateTime utcNow)
    {
        var current = GetCurrent(channelId);

        if (utcNow - (_lastSwitch.TryGetValue(channelId, out var last) ? last : DateTime.MinValue) < TimeSpan.FromSeconds(_minHoldSec))
        {
            return new StreamSwitchDecision(current, null);
        }

        var eventScore = recentEvents
            .Where(e => e.AtUtc >= utcNow.AddSeconds(-_eventWindowSec))
            .Sum(e => _eventWeights.GetValueOrDefault(e.EventType));

        if (eventScore > 0 && current == StreamKind.Sub)
        {
            return new StreamSwitchDecision(StreamKind.Main, $"event-burst:{eventScore}");
        }

        if (current == StreamKind.Main &&
            (currentMainBytesPerSec >= _mainBandwidthLimitBytesPerSec || activeViewers == 0))
        {
            return new StreamSwitchDecision(StreamKind.Sub, activeViewers == 0 ? "no-viewers" : "bandwidth-peak");
        }

        return new StreamSwitchDecision(current, null);
    }

    /// <summary>調度層採納決策後回報，更新頻道現行串流與切換時間。</summary>
    public void ApplySwitch(long channelId, StreamKind target, DateTime utcNow)
    {
        _current[channelId] = target;
        _lastSwitch[channelId] = utcNow;
    }
}