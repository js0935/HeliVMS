using System.Globalization;

namespace HeliVMS.Shared;

/// <summary>
/// 回放偏好的儲存值還原（速度／分割數／自動接續／循環）。
/// </summary>
/// <remarks>
/// <para>
/// 操作員每次進入回放都要重選速度、分割數與兩個勾選（回看事故時通常是 4× 或 8×、四分割、
/// 自動接續），而這些值本來就該跟著人而不是跟著視窗。存的是字串（app_settings 一律字串），
/// 還原時要能容忍壞值：索引超出目前選單長度（例如日後刪掉某個分割選項）要退回預設，
/// 而不是讓 ComboBox 停在 -1（SelectedIndex 無效＝沒有選取，畫面看起來像壞掉）。
/// </para>
/// <para>
/// 抽成純函式是為了能行為測試：桌面端沒有 WPF 執行期測試，而「壞值退回預設」正是
/// 只有真的升過版、真的改過選單才會踩到的地方。
/// </para>
/// </remarks>
public static class PlaybackPreferenceValues
{
    /// <summary>把儲存的字串還原成布林：<c>"1"</c>＝開、<c>"0"</c>＝關、其餘＝<paramref name="fallback"/>。</summary>
    public static bool Bool(string? stored, bool fallback) => stored switch
    {
        "1" => true,
        "0" => false,
        _ => fallback,
    };

    /// <summary>
    /// 把儲存的索引夾回合法範圍：無法解析或超出 <paramref name="count"/> 時回 <paramref name="fallback"/>。
    /// </summary>
    public static int Index(string? stored, int count, int fallback)
        => count > 0
            && int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= 0
            && value < count
                ? value
                : fallback;
}
