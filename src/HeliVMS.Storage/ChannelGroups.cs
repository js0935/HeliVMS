using HeliVMS.Shared;

namespace HeliVMS.Storage;

/// <summary>監看群組：監控中心的一個頁簽，內含一組自選頻道。</summary>
/// <param name="Id">群組 Id。</param>
/// <param name="Name">顯示名稱（唯一）。</param>
/// <param name="Position">頁簽順序（0 基）。「全部」固定在頁簽列最前面，群組從其後依 <see cref="Position"/> 排序。</param>
public sealed record ChannelGroup(long Id, string Name, int Position);

/// <summary>
/// 監看群組倉儲（v49）：群組的增刪改名，以及「頻道↔群組」的歸屬與群組內排序。
/// </summary>
/// <remarks>
/// <para>
/// 一台攝影機最多屬於一個群組——這條規則由 <c>channel_group_members</c> 的 <c>UNIQUE(channel_id)</c> 在資料層保證，
/// 不是靠每個呼叫端記得先解除舊歸屬。因此「改派」＝先刪除該頻道所有既有歸屬、再附加到目標群組末端。
/// </para>
/// <para>
/// 群組內順序用 <c>position</c>（0 基）表示；<see cref="Move"/> 先在記憶體用
/// <see cref="ChannelLayout.Move"/> 重排後整批覆寫，和監看牆拖曳調順序共用同一套「搬移而非交換」語意。
/// </para>
/// </remarks>
public sealed class ChannelGroupRepository
{
    private readonly SqliteStore _store;

    public ChannelGroupRepository(SqliteStore store) => _store = store;

    /// <summary>列出所有群組（依頁簽順序）。</summary>
    public IReadOnlyList<ChannelGroup> List()
    {
        return _store.Query(
            "SELECT id, name, position FROM channel_groups ORDER BY position, id;",
            static r =>
            {
                var list = new List<ChannelGroup>();
                while (r.Read())
                {
                    list.Add(new ChannelGroup(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
                }

                return list;
            });
    }

    public ChannelGroup? Get(long id)
    {
        return _store.Query(
            "SELECT id, name, position FROM channel_groups WHERE id = $id;",
            static r => r.Read() ? new ChannelGroup(r.GetInt64(0), r.GetString(1), r.GetInt32(2)) : null,
            cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>建立群組，回傳新 Id；名稱重複時丟 <see cref="ArgumentException"/>。</summary>
    public long Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (NameExists(name, exceptId: null))
        {
            throw new ArgumentException($"群組名稱重複：{name}。", nameof(name));
        }

        // 新群組排在最後一個頁簽之後；MAX 為空時 COALESCE 給 -1，+1 後第一位即 0。
        var position = _store.Query<long>(
            "SELECT COALESCE(MAX(position), -1) + 1 FROM channel_groups;",
            static r => r.Read() ? r.GetInt64(0) : 0);

        _store.Execute(
            "INSERT INTO channel_groups (name, position) VALUES ($n, $p);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$p", position);
            });

        return _store.Query<long>("SELECT last_insert_rowid();", static r => r.Read() ? r.GetInt64(0) : 0);
    }

    /// <summary>重新命名群組；名稱重複（其他群組已用）時丟 <see cref="ArgumentException"/>。</summary>
    public void Rename(long id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (NameExists(name, exceptId: id))
        {
            throw new ArgumentException($"群組名稱重複：{name}。", nameof(name));
        }

        _store.Execute(
            "UPDATE channel_groups SET name = $n WHERE id = $id;",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$n", name);
            });
    }

    /// <summary>刪除群組；成員歸屬連帶刪除（外鍵 CASCADE）。</summary>
    public void Delete(long id)
    {
        _store.Execute("DELETE FROM channel_groups WHERE id = $id;", cmd => cmd.Parameters.AddWithValue("$id", id));
    }

    /// <summary>該群組的頻道 Id（依群組內顯示順序）。</summary>
    public IReadOnlyList<int> ListChannelIds(long groupId)
    {
        return _store.Query(
            "SELECT channel_id FROM channel_group_members WHERE group_id = $g ORDER BY position, channel_id;",
            static r =>
            {
                var list = new List<int>();
                while (r.Read())
                {
                    list.Add(r.GetInt32(0));
                }

                return list;
            },
            cmd => cmd.Parameters.AddWithValue("$g", groupId));
    }

    /// <summary>該頻道所屬的群組 Id；未歸屬時為 null。</summary>
    public long? GroupOf(int channelId)
    {
        return _store.Query<long?>(
            "SELECT group_id FROM channel_group_members WHERE channel_id = $c;",
            static r => r.Read() ? r.GetInt64(0) : null,
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
    }

    /// <summary>
    /// 把頻道指派到群組（附加到該群組末端）。
    /// 因為一台僅屬一個群組，會先移除該頻道所有既有歸屬——從舊群組改派到新群組即為此一步。
    /// </summary>
    public void Assign(int channelId, long groupId)
    {
        _store.Execute(
            "DELETE FROM channel_group_members WHERE channel_id = $c;",
            cmd => cmd.Parameters.AddWithValue("$c", channelId));

        var position = _store.Query<long>(
            "SELECT COALESCE(MAX(position), -1) + 1 FROM channel_group_members WHERE group_id = $g;",
            static r => r.Read() ? r.GetInt64(0) : 0,
            cmd => cmd.Parameters.AddWithValue("$g", groupId));

        _store.Execute(
            "INSERT INTO channel_group_members (group_id, channel_id, position) VALUES ($g, $c, $p);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$g", groupId);
                cmd.Parameters.AddWithValue("$c", channelId);
                cmd.Parameters.AddWithValue("$p", position);
            });
    }

    /// <summary>把頻道移出群組（未歸屬時為 no-op）。</summary>
    public void Remove(int channelId)
    {
        _store.Execute(
            "DELETE FROM channel_group_members WHERE channel_id = $c;",
            cmd => cmd.Parameters.AddWithValue("$c", channelId));
    }

    /// <summary>
    /// 在群組內把頻道往前／往後搬 <paramref name="delta"/> 個位置（負為前）。
    /// 越界時夾到端點；頻道不在該群組則 no-op。
    /// </summary>
    public void Move(long groupId, int channelId, int delta)
    {
        var ids = ListChannelIds(groupId).ToList();
        var from = ids.IndexOf(channelId);
        if (from < 0)
        {
            return;
        }

        var to = Math.Clamp(from + delta, 0, ids.Count - 1);
        if (to == from)
        {
            return;
        }

        var moved = ChannelLayout.Move(ids, from, to);
        _store.Execute(
            "DELETE FROM channel_group_members WHERE group_id = $g;",
            cmd => cmd.Parameters.AddWithValue("$g", groupId));

        for (var i = 0; i < moved.Count; i++)
        {
            var position = i;
            var channelIdToWrite = moved[i];
            _store.Execute(
                "INSERT INTO channel_group_members (group_id, channel_id, position) VALUES ($g, $c, $p);",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$g", groupId);
                    cmd.Parameters.AddWithValue("$c", channelIdToWrite);
                    cmd.Parameters.AddWithValue("$p", position);
                });
        }
    }

    private bool NameExists(string name, long? exceptId)
    {
        return _store.Query<bool>(
            exceptId is long id
                ? "SELECT EXISTS(SELECT 1 FROM channel_groups WHERE name = $n AND id <> $id);"
                : "SELECT EXISTS(SELECT 1 FROM channel_groups WHERE name = $n);",
            static r => r.Read() && r.GetInt32(0) == 1,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$n", name);
                if (exceptId is long id)
                {
                    cmd.Parameters.AddWithValue("$id", id);
                }
            });
    }
}
