namespace HeliVMS.Shared;

/// <summary>
/// 頻道預設命名：未命名的新頻道依序取「攝影機01、攝影機02…」——第一個還沒被用過的序號。
/// </summary>
/// <remarks>
/// 抽成純函式是為了能行為測試：桌面端沒有 WPF 執行期測試（見 <c>UiWiringContractTests</c>），
/// 而「刪掉中間一台後再新增會撞名」正是那種只有真的建過資料才會出現、光看畫面不會發現的缺陷。
/// </remarks>
public static class ChannelNaming
{
    /// <summary>
    /// 依現有名稱與當地語系格式（例如 <c>"攝影機{0:00}"</c>），回傳第一個尚未使用的預設名稱。
    /// </summary>
    /// <remarks>
    /// 從 1 往上找第一個空缺，而不是用「目前數量 + 1」：先有攝影機01、攝影機02，
    /// 刪掉 01 後再新增，數量 + 1 會又得到攝影機02（重複）；找空缺才會回到攝影機01。
    /// </remarks>
    public static string NextDefault(IEnumerable<string> existingNames, string format)
    {
        var used = new HashSet<string>(existingNames, StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var candidate = string.Format(format, n);
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 判斷名稱是否為預設命名（例如 <c>"攝影機03"</c>）：當地語系格式的前綴加尾端數字。
    /// </summary>
    /// <remarks>
    /// 用途是「指定監看格位時只讓預設命名的頻道跟著格號走」。操作員自取的名稱（例如「大門」）
    /// 若被覆蓋，等他發現時已經不會知道原本叫什麼，所以必須先分辨出來才改。
    /// </remarks>
    public static bool MatchesDefault(string name, string format)
    {
        var brace = format.IndexOf('{');
        if (brace < 0)
        {
            return false;
        }

        var prefix = format[..brace];
        if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in name.AsSpan(prefix.Length))
        {
            if (!char.IsDigit(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 判斷名稱是否仍「沿用它綁定的裝置名稱」：從未命名過的匯入頻道才算，
    /// 一旦被改成預設編號或操作員自取的名稱就不再符合。
    /// </summary>
    /// <remarks>
    /// 用途是「只把從沒命名過的匯入頻道補上格位預設名」。刻意用裝置名稱相等、而不是
    /// <see cref="MatchesDefault"/>：監看牆上的名稱要跟著攝影機搬（拖曳調順序時名稱一起移動），
    /// 若把既有的「攝影機NN」也視為可自動命名，每次載入／拖曳就會依格位重新編號，
    /// 把操作員排好的順序又改回去。
    /// </remarks>
    public static bool CarriesDeviceName(string name, string? deviceName)
        => !string.IsNullOrEmpty(deviceName)
            && string.Equals(name, deviceName, StringComparison.Ordinal);

    /// <summary>
    /// 依監看格位順序，為「預設命名」的頻道算出應該叫什麼（第一格＝攝影機01、第二格＝攝影機02…）；
    /// 自訂名稱保留不動。回傳與輸入等長的清單，<see langword="null"/> 表示該格不需改名。
    /// </summary>
    /// <param name="channels">依格位排序的頻道（名稱與「是否為預設命名」）。</param>
    /// <param name="format">當地語系預設格式（例如 <c>"攝影機{0:00}"</c>）。</param>
    /// <remarks>
    /// 名稱跟著格位走：把「攝影機05」搬到第 3 格後應該變「攝影機03」，否則操作員靠名稱找機位會找錯。
    /// 自訂名稱（例如「大門」）先全部保留，預設命名再從未被佔用的序號取，避免改名後撞上自訂名稱。
    /// </remarks>
    public static IReadOnlyList<string?> ReassignDefaultNames(
        IReadOnlyList<(string Name, bool IsDefault)> channels, string format)
    {
        var reserved = new HashSet<string>(
            channels.Where(c => !c.IsDefault).Select(c => c.Name),
            StringComparer.Ordinal);

        var result = new string?[channels.Count];
        for (var i = 0; i < channels.Count; i++)
        {
            var (name, isDefault) = channels[i];
            if (!isDefault)
            {
                continue;
            }

            var desired = string.Format(format, i + 1);
            if (reserved.Contains(desired))
            {
                desired = NextDefault(reserved, format);
            }

            reserved.Add(desired);
            if (!string.Equals(name, desired, StringComparison.Ordinal))
            {
                result[i] = desired;
            }
        }

        return result;
    }
}
