using System.Globalization;
using System.Text.Json;

namespace HeliVMS.Shared;

/// <summary>
/// 監控中心「佈局記憶」（<c>ui.json</c>）的內容：每個頁簽各自記住格數，並記住最後停留的頁簽。
/// </summary>
/// <remarks>
/// <para>
/// 症狀：頁簽切換後沿用上一個頁簽的格數。某個只有 4 台攝影機的群組若排成 16 格，畫面大半是空的；
/// 而「全部」頁簽排成 4 格又看不完。每個頁簽各自記格數才符合操作員切頁簽時的期待。
/// </para>
/// <para>
/// 格式：<c>{ "layout": 3, "activeTab": "7", "cells": { "all": 16, "7": 4 } }</c>。
/// 舊版只有單一 <c>"cells": int</c>（＝「全部」頁簽），仍要能讀，否則升級後操作員的版面會被歸零。
/// 抽成純型別是為了能行為測試：桌面端沒有 WPF 執行期測試，而「舊檔相容」與「多頁簽互不干擾」
/// 正是只有真的升過版、真的切過頁簽才會踩到的地方。
/// </para>
/// </remarks>
public sealed class MonitorLayoutPrefs
{
    /// <summary>「全部」頁簽的鍵；群組頁簽以群組 Id 的字串為鍵。</summary>
    public const string AllTab = "all";

    /// <summary>頁簽鍵 → 該頁簽記住的格數。</summary>
    public Dictionary<string, int> Cells { get; } = new(StringComparer.Ordinal);

    /// <summary>最後停留的頁簽鍵；無法辨識時為 <see cref="AllTab"/>。</summary>
    public string ActiveTab { get; set; } = AllTab;

    /// <summary>最後一次佈局下拉的索引；僅在使用者尚未留下任何格數時作為退路。</summary>
    public int? LayoutIndex { get; set; }

    /// <summary>頁簽鍵：<paramref name="groupId"/> 為 <see langword="null"/> 時回 <see cref="AllTab"/>。</summary>
    public static string TabKey(long? groupId)
        => groupId is long id ? id.ToString(CultureInfo.InvariantCulture) : AllTab;

    /// <summary>最後停留頁簽對應的群組 Id；「全部」或無法辨識時為 <see langword="null"/>。</summary>
    public long? ActiveGroupId
        => ActiveTab == AllTab ? null
            : long.TryParse(ActiveTab, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id
            : null;

    /// <summary>讀取指定頁簽記住的格數；沒有記錄時回 <see langword="null"/>。</summary>
    public int? CellsFor(long? groupId)
        => Cells.TryGetValue(TabKey(groupId), out var cells) ? cells : null;

    /// <summary>記住指定頁簽的格數（非正數忽略，避免寫進 0 格而讓監看牆空白）。</summary>
    public void Remember(long? groupId, int cells)
    {
        if (cells > 0)
        {
            Cells[TabKey(groupId)] = cells;
        }
    }

    /// <summary>解析 <c>ui.json</c>；空字串、非物件或損毀內容一律回預設值（全部頁簽、無格數）。</summary>
    public static MonitorLayoutPrefs Parse(string? json)
    {
        var prefs = new MonitorLayoutPrefs();
        if (string.IsNullOrWhiteSpace(json))
        {
            return prefs;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return prefs;
            }

            if (root.TryGetProperty("activeTab", out var active) && active.ValueKind == JsonValueKind.String)
            {
                var tab = active.GetString();
                prefs.ActiveTab = string.IsNullOrWhiteSpace(tab) ? AllTab : tab!;
            }

            if (root.TryGetProperty("cells", out var cells))
            {
                if (cells.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in cells.EnumerateObject())
                    {
                        if (entry.Value.TryGetInt32(out var n) && n > 0)
                        {
                            prefs.Cells[entry.Name] = n;
                        }
                    }
                }
                else if (cells.TryGetInt32(out var single) && single > 0)
                {
                    // 舊版單一格數＝「全部」頁簽。
                    prefs.Cells[AllTab] = single;
                }
            }

            if (root.TryGetProperty("layout", out var layout) && layout.TryGetInt32(out var idx))
            {
                prefs.LayoutIndex = idx;
            }
        }
        catch (JsonException)
        {
            // 損毀的偏好檔不該讓主視窗開不起來，退回預設。
        }

        return prefs;
    }

    /// <summary>序列化為 <c>ui.json</c>；<paramref name="layoutIndex"/> 為目前下拉索引（退路用）。</summary>
    public string ToJson(int layoutIndex)
    {
        var payload = new
        {
            layout = layoutIndex,
            activeTab = ActiveTab,
            cells = Cells,
        };
        return JsonSerializer.Serialize(payload);
    }
}
