namespace HeliVMS.Storage;

/// <summary>智慧牆版面（M105，§14.7 #14）：rows×cols 網格。</summary>
public sealed record SmartwallLayout(
    long Id,
    string Name,
    int Rows,
    int Cols,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>版面方塊（M105，§14.7 #14）：row/col 為 0 起算，span 覆蓋格子；view=0 單鏡頭、1 馬賽克。</summary>
public sealed record LayoutTile(
    long Id,
    long LayoutId,
    int Row,
    int Col,
    int RowSpan,
    int ColSpan,
    int? ChannelId,
    int View,
    int Position);

/// <summary>
/// 智慧牆版面倉儲（M105，v39）：CreateLayout／RenameLayout／ListLayouts／AddTile（越界與重疊
/// 校驗）／GetTiles／RemoveTile。版面名稱唯一；方格尺寸限 1..16。
/// </summary>
public sealed class SmartwallLayoutRepository
{
    private readonly SqliteStore _store;

    public SmartwallLayoutRepository(SqliteStore store) => _store = store;

    public long CreateLayout(string name, int rows, int cols)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cols, 16);

        var exists = _store.Query<bool>(
            "SELECT EXISTS(SELECT 1 FROM smartwall_layouts WHERE name = $n);",
            r => r.Read() && r.GetInt32(0) == 1,
            cmd => cmd.Parameters.AddWithValue("$n", name));
        if (exists)
        {
            throw new ArgumentException($"版面名稱重複：{name}。", nameof(name));
        }

        var now = DateTime.UtcNow;
        _store.Execute(
            """
            INSERT INTO smartwall_layouts (name, rows, cols, created_at, updated_at)
            VALUES ($n, $r, $c, $t, $t);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$r", rows);
                cmd.Parameters.AddWithValue("$c", cols);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(now));
            });
        return _store.Query<long>(
            "SELECT last_insert_rowid();", r => r.Read() ? r.GetInt64(0) : 0);
    }

    public void RenameLayout(long layoutId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _store.Execute(
            "UPDATE smartwall_layouts SET name = $n, updated_at = $t WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", layoutId);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$t", SqliteStore.Iso(DateTime.UtcNow));
            });
    }

    public IReadOnlyList<SmartwallLayout> ListLayouts()
    {
        return _store.Query(
            "SELECT id, name, rows, cols, created_at, updated_at FROM smartwall_layouts ORDER BY id;",
            reader =>
            {
                var rows = new List<SmartwallLayout>();
                while (reader.Read())
                {
                    rows.Add(new SmartwallLayout(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        SqliteStore.FromIso(reader.GetString(4)),
                        SqliteStore.FromIso(reader.GetString(5))));
                }

                return rows;
            });
    }

    /// <summary>加入方塊；回傳 Id。越界或與既有方塊重疊→<see cref="ArgumentException"/>。</summary>
    public long AddTile(
        long layoutId,
        int row,
        int col,
        int rowSpan,
        int colSpan,
        int? channelId,
        int view,
        int position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(col, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowSpan, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(colSpan, 1);

        var (rows, cols) = _store.Query<(int, int)>(
            "SELECT rows, cols FROM smartwall_layouts WHERE id = $id;",
            r => r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0),
            cmd => cmd.Parameters.AddWithValue("$id", layoutId));

        if (rows == 0)
        {
            throw new ArgumentException("版面不存在。", nameof(layoutId));
        }

        if (!LayoutGrid.IsTileInBounds(rows, cols, row, col, rowSpan, colSpan))
        {
            throw new ArgumentException(
                $"方塊超出版面：({row},{col})＋{rowSpan}x{colSpan} 不合 {rows}x{cols}。");
        }

        var existing = _store.Query(
            "SELECT id, layout_id, row, col, rowspan, colspan, channel_id, view, position" +
            " FROM smartwall_tiles WHERE layout_id = $id;",
            reader =>
            {
                var list = new List<LayoutTile>();
                while (reader.Read())
                {
                    list.Add(new LayoutTile(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8)));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$id", layoutId));

        if (LayoutGrid.FindOverlap(existing, row, col, rowSpan, colSpan) is { } hit)
        {
            throw new ArgumentException($"方塊重疊既有方塊 #{hit.Id}（({hit.Row},{hit.Col})＋{hit.RowSpan}x{hit.ColSpan}）。");
        }

        _store.Execute(
            """
            INSERT INTO smartwall_tiles (layout_id, row, col, rowspan, colspan, channel_id, view, position)
            VALUES ($l, $r, $c, $rs, $cs, $ch, $v, $p);
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$l", layoutId);
                cmd.Parameters.AddWithValue("$r", row);
                cmd.Parameters.AddWithValue("$c", col);
                cmd.Parameters.AddWithValue("$rs", rowSpan);
                cmd.Parameters.AddWithValue("$cs", colSpan);
                if (channelId is { } ch)
                {
                    cmd.Parameters.AddWithValue("$ch", ch);
                }
                else
                {
                    cmd.Parameters.AddWithValue("$ch", DBNull.Value);
                }

                cmd.Parameters.AddWithValue("$v", view);
                cmd.Parameters.AddWithValue("$p", position);
            });
        return _store.Query<long>(
            "SELECT last_insert_rowid();", r => r.Read() ? r.GetInt64(0) : 0);
    }

    public IReadOnlyList<LayoutTile> GetTiles(long layoutId)
    {
        return _store.Query(
            "SELECT id, layout_id, row, col, rowspan, colspan, channel_id, view, position" +
            " FROM smartwall_tiles WHERE layout_id = $id ORDER BY position, id;",
            reader =>
            {
                var list = new List<LayoutTile>();
                while (reader.Read())
                {
                    list.Add(new LayoutTile(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6),
                        reader.GetInt32(7),
                        reader.GetInt32(8)));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$id", layoutId));
    }

    public void RemoveTile(long tileId)
    {
        _store.Execute(
            "DELETE FROM smartwall_tiles WHERE id = $id;",
            cmd => cmd.Parameters.AddWithValue("$id", tileId));
    }
}

/// <summary>
/// 版面網格純校驗（M105，純 BCL）：方塊是否越界、與既有方塊是否重疊（矩形交集）。
/// </summary>
public static class LayoutGrid
{
    public static bool IsTileInBounds(int rows, int cols, int row, int col, int rowSpan, int colSpan)
    {
        return row >= 0 && col >= 0 && rowSpan >= 1 && colSpan >= 1
            && row + rowSpan <= rows && col + colSpan <= cols;
    }

    /// <summary>找出與 (row,col)+span 重疊的既有方塊；無則為 null。</summary>
    public static LayoutTile? FindOverlap(
        IEnumerable<LayoutTile> existing,
        int row,
        int col,
        int rowSpan,
        int colSpan)
    {
        foreach (var tile in existing)
        {
            var overlapRows = Math.Min(row + rowSpan, tile.Row + tile.RowSpan)
                - Math.Max(row, tile.Row);
            var overlapCols = Math.Min(col + colSpan, tile.Col + tile.ColSpan)
                - Math.Max(col, tile.Col);
            if (overlapRows > 0 && overlapCols > 0)
            {
                return tile;
            }
        }

        return null;
    }
}

/// <summary>
/// 作業視窗（§14.7 #14 智慧牆）時間常數：告警單格高亮 5s、馬賽克保留最近 300s。
/// </summary>
public static class SmartwallTimings
{
    public static readonly TimeSpan AlarmHighlight = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MosaicKeepLast = TimeSpan.FromSeconds(300);
}