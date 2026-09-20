using Microsoft.Data.Sqlite;

namespace HeliVMS.Storage;

/// <summary>分析情境模組種類（M52，§14.7 #6）。</summary>
public static class AnalyticsModuleKinds
{
    public const string LineCross = "line_cross";
    public const string Intrusion = "intrusion";
    public const string Crowd = "crowd";
    public const string Loitering = "loitering";
    public const string Stationary = "stationary";
    public const string Traffic = "traffic";
    public const string Heatmap = "heatmap";

    public static IReadOnlyList<string> All { get; } = new[]
        { LineCross, Intrusion, Crowd, Loitering, Stationary, Traffic, Heatmap };

    public static bool IsValid(string module) => All.Contains(module);

    /// <summary>雙點線段模組（<see cref="LineCross"/>、<see cref="Traffic"/>）允許 2 點幾何（M58）。</summary>
    public static bool IsLineModule(string? module) => module is LineCross or Traffic;
}

/// <summary>跨線方向（M52）。</summary>
public static class AnalyticsDirections
{
    public const string Both = "both";
    public const string AToB = "a_to_b";
    public const string BToA = "b_to_a";

    public static bool IsValid(string direction) => direction is Both or AToB or BToA;
}

/// <summary>分析情境區（M52，§14.7 #6）。</summary>
public sealed record AnalyticsZoneRecord(
    int Id,
    string Name,
    int ChannelId,
    string Module,
    bool Enabled,
    string Polygon,
    string Direction,
    int MinCount,
    int DwellSeconds,
    string CreatedAt);

/// <summary>分析情境區存取（M52，§14.7 #6）。</summary>
public sealed class AnalyticsZoneRepository
{
    private const string Columns =
        "id, name, channel_id, module, enabled, polygon, direction, min_count, dwell_seconds, created_at";

    private readonly SqliteStore _store;

    public AnalyticsZoneRepository(SqliteStore store)
    {
        _store = store;
    }

    public int Add(
        string name,
        int channelId,
        string module,
        string polygon,
        string direction = AnalyticsDirections.Both,
        int minCount = 0,
        int dwellSeconds = 0,
        DateTime? createdUtc = null)
    {
        Validate(name, module, polygon, direction, minCount, dwellSeconds);

        return _store.Query(
            $"""
            INSERT INTO analytics_zones
                (name, channel_id, module, enabled, polygon, direction, min_count, dwell_seconds, created_at)
            VALUES
                ($n, $c, $m, 1, $p, $d, $min, $dwell, $t);
            SELECT last_insert_rowid();
            """,
            static r =>
            {
                r.Read();
                return (int)r.GetInt64(0);
            },
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$m", module);
                cmd.Parameters.AddWithValue("$p", polygon);
                cmd.Parameters.AddWithValue("$d", direction);
                cmd.Parameters.AddWithValue("$min", minCount);
                cmd.Parameters.AddWithValue("$dwell", dwellSeconds);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(createdUtc ?? DateTime.UtcNow));
            });
    }

    public IReadOnlyList<AnalyticsZoneRecord> List()
        => _store.Query(
            $"SELECT {Columns} FROM analytics_zones ORDER BY id;",
            ReadAll);

    public IReadOnlyList<AnalyticsZoneRecord> ListByChannel(int channelId)
        => _store.Query(
            $"SELECT {Columns} FROM analytics_zones WHERE channel_id = $c ORDER BY id;",
            ReadAll,
            cmd => cmd.Parameters.AddWithValue("$c", channelId));

    public AnalyticsZoneRecord? Get(int id)
        => _store.Query(
            $"SELECT {Columns} FROM analytics_zones WHERE id = $id;",
            static r => r.Read() ? Read(r) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));

    public void Update(int id, string name, string polygon, string direction, int minCount, int dwellSeconds)
    {
        var module = Get(id)?.Module;
        Validate(name, module, polygon, direction, minCount, dwellSeconds);

        _store.Execute(
            """
            UPDATE analytics_zones
            SET name = $n, polygon = $p, direction = $d, min_count = $min, dwell_seconds = $dwell
            WHERE id = $id;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$p", polygon);
                cmd.Parameters.AddWithValue("$d", direction);
                cmd.Parameters.AddWithValue("$min", minCount);
                cmd.Parameters.AddWithValue("$dwell", dwellSeconds);
                cmd.Parameters.AddWithValue("$id", id);
            });
    }

    public void SetEnabled(int id, bool enabled)
        => _store.Execute(
            "UPDATE analytics_zones SET enabled = $e WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$id", id);
            });

    public void Delete(int id)
        => _store.Execute(
            "DELETE FROM analytics_zones WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", id));

    private static void Validate(string name, string? module, string polygon, string direction, int minCount, int dwellSeconds)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("名稱不可為空", nameof(name));
        }

        if (module is not null && !AnalyticsModuleKinds.IsValid(module))
        {
            throw new ArgumentException("無效的分析模組", nameof(module));
        }

        var minPoints = AnalyticsModuleKinds.IsLineModule(module)
            ? AnalyticsPolygon.MinLinePoints
            : AnalyticsPolygon.MinPoints;
        if (!AnalyticsPolygon.IsValid(polygon, minPoints))
        {
            throw new ArgumentException(
                minPoints == AnalyticsPolygon.MinLinePoints
                    ? "線段需至少 2 個 0..1 座標點"
                    : "多邊形需至少 3 個 0..1 座標點",
                nameof(polygon));
        }

        if (!AnalyticsDirections.IsValid(direction))
        {
            throw new ArgumentException("無效的方向", nameof(direction));
        }

        if (minCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minCount));
        }

        if (dwellSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dwellSeconds));
        }
    }

    private static IReadOnlyList<AnalyticsZoneRecord> ReadAll(SqliteDataReader r)
    {
        var list = new List<AnalyticsZoneRecord>();
        while (r.Read())
        {
            list.Add(Read(r));
        }

        return list;
    }

    private static AnalyticsZoneRecord Read(SqliteDataReader r)
        => new(
            r.GetInt32(0),
            r.GetString(1),
            r.GetInt32(2),
            r.GetString(3),
            r.GetInt32(4) != 0,
            r.GetString(5),
            r.GetString(6),
            r.GetInt32(7),
            r.GetInt32(8),
            r.GetString(9));
}
