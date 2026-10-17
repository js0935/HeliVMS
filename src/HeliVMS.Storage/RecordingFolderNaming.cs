using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

/// <summary>
/// 錄影資料夾命名（M311）。
///
/// <para>
/// 錄影原本一律寫進 <c>ch{id:000}</c>（例如 <c>ch001</c>），操作員在磁碟上只看得到編號，
/// 想知道「哪個資料夾是哪台攝影機」得回程式裡對照頻道編號。這裡把資料夾名稱改成頻道名稱，
/// 讓磁碟上的目錄一看就懂。
/// </para>
/// <para>
/// 兩個關鍵不變式：①**檔名安全**——頻道名稱是使用者自由輸入，可能含 <c>\ / : * ? " &lt; &gt; |</c>、
/// 結尾的空白或句點、或 Windows 保留名（<c>CON</c>、<c>COM1</c>…），直接當資料夾名會讓
/// <see cref="Path.Combine(string, string)"/> 或建立目錄直接失敗。②**唯一**——多個頻道可以同名，
/// 而資料夾必須一頻道一個；同名時附加 <c>_ch{id}</c> 消歧，且**排序固定用頻道 id**，
/// 否則錄影端與搬移端會算出不同的名稱，把已經搬好的目錄又搬一次。
/// </para>
/// </summary>
public static class RecordingFolderNaming
{
    /// <summary>舊版以頻道編號命名的樣式；保留作為無名稱頻道的後備。</summary>
    public static string LegacyName(int channelId) => $"ch{channelId:000}";

    /// <summary>清理後的名稱長度上限；太長會讓整條路徑逼近 MAX_PATH，且沒有實質辨識價值。</summary>
    private const int MaxNameLength = 80;

    // Windows 保留裝置名稱：即使加了副檔名（如 "CON.mp4"）仍會被拒。
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 把頻道名稱洗成可安全當資料夾名稱的字串；名稱空白時退回 <see cref="LegacyName"/>。
    /// 回傳值保證：非空、不含非法字元、不以句點或空白結尾、不是保留名。
    /// </summary>
    public static string Sanitize(string? channelName, int channelId)
    {
        var name = channelName ?? string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch).ToArray();
        var cleaned = new string(chars);

        // 尾端的句點／空白在 Windows 上會被默默剝除，導致實際目錄名與資料庫記錄不一致。
        cleaned = cleaned.TrimEnd('.', ' ');

        if (cleaned.Length > MaxNameLength)
        {
            cleaned = cleaned[..MaxNameLength].TrimEnd('.', ' ');
        }

        if (cleaned.Length == 0)
        {
            return LegacyName(channelId);
        }

        if (ReservedNames.Contains(cleaned))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    /// <summary>
    /// 為每個頻道算出資料夾名稱（依頻道 id 排序、對同名者消歧）。
    /// 錄影端與搬移端都必須呼叫這個方法，且必須看到同一組頻道，算出來的名稱才會一致。
    /// </summary>
    public static IReadOnlyDictionary<int, string> Resolve(IEnumerable<ChannelInfo> channels)
    {
        var ordered = channels.Where(c => c.Id > 0).OrderBy(c => c.Id).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<int, string>(ordered.Count);

        foreach (var channel in ordered)
        {
            var candidate = Sanitize(channel.Name, channel.Id);
            if (!used.Add(candidate))
            {
                // 同名：附加頻道 id（唯一且穩定）。極少數情況連附加後也撞名（例如別的頻道
                // 就叫 "foo_ch002"），再往後加序號，直到唯一為止。
                var disambiguated = $"{candidate}_ch{channel.Id:000}";
                var suffix = 1;
                while (!used.Add(disambiguated))
                {
                    disambiguated = $"{candidate}_ch{channel.Id:000}_{suffix++}";
                }

                candidate = disambiguated;
            }

            map[channel.Id] = candidate;
        }

        return map;
    }

    /// <summary>
    /// 算出單一頻道的資料夾名稱（未建庫時回 <c>null</c>）。
    /// 開錄的兩條路（排程與監看牆手動錄影）都用這個方法，確保同一頻道只會有一個資料夾。
    /// </summary>
    public static string? ResolveFor(SqliteStore store, int channelId)
        => Resolve(new ChannelRepository(store).List()).TryGetValue(channelId, out var name) ? name : null;
}
