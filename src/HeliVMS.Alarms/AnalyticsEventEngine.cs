using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 分析情境事件引擎（M52，§14.7 #6）：載入分析區，將 AI 偵測幀交由
/// <see cref="AnalyticsZoneEvaluator"/> 評估，並把命中結果寫入 <c>alarm_events</c>。
/// </summary>
public sealed class AnalyticsEventEngine
{
    private readonly AlarmEventRepository _events;
    private readonly AnalyticsZoneRepository _zoneRepo;
    private readonly AnalyticsZoneEvaluator _evaluator = new();
    private readonly object _gate = new();
    private IReadOnlyList<AnalyticsZone> _zones = Array.Empty<AnalyticsZone>();

    public AnalyticsEventEngine(SqliteStore store)
    {
        _events = new AlarmEventRepository(store);
        _zoneRepo = new AnalyticsZoneRepository(store);
    }

    public event EventHandler<AlarmEventRecord>? EventInserted;

    public int ZoneCount
    {
        get
        {
            lock (_gate)
            {
                return _zones.Count;
            }
        }
    }

    /// <summary>重新載入分析區並重置跨幀狀態。</summary>
    public void LoadZones()
    {
        var zones = _zoneRepo.List().Select(AnalyticsZone.From).ToArray();
        lock (_gate)
        {
            _zones = zones;
            _evaluator.Reset();
        }
    }

    /// <summary>以既有 AI 偵測幀評估分析區，回傳本幀新插入的事件。</summary>
    public IReadOnlyList<AlarmEventRecord> OnDetections(int channelId, DetectionsFrame frame)
    {
        IReadOnlyList<AnalyticsZone> zones;
        lock (_gate)
        {
            zones = _zones.Where(z => z.ChannelId == channelId).ToArray();
        }

        if (zones.Count == 0)
        {
            return Array.Empty<AlarmEventRecord>();
        }

        var detections = frame.Items
            .Select(d => new AnalyticsDetection(d.Class, d.X + (d.W / 2.0), d.Y + (d.H / 2.0), d.Confidence))
            .ToArray();

        IReadOnlyList<AnalyticsResult> results;
        lock (_gate)
        {
            results = _evaluator.Evaluate(frame.SnapshotUtc, detections, zones);
        }

        if (results.Count == 0)
        {
            return Array.Empty<AlarmEventRecord>();
        }

        var inserted = new List<AlarmEventRecord>();
        foreach (var result in results)
        {
            var detail = $"{result.ZoneName}：{result.Detail}";
            var id = _events.Insert(channelId, result.EventType, frame.SnapshotUtc, null, detail);
            var record = new AlarmEventRecord
            {
                Id = id,
                ChannelId = channelId,
                EventType = result.EventType,
                StartUtc = frame.SnapshotUtc,
                EndUtc = frame.SnapshotUtc,
                Detail = detail,
            };

            inserted.Add(record);
            EventInserted?.Invoke(this, record);
        }

        return inserted;
    }
}
