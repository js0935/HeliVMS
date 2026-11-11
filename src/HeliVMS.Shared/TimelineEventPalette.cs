namespace HeliVMS.Shared;

/// <summary>
/// 時間軸事件刻度的色票（純資料；WPF 由桌面端轉成 Brush）。
/// </summary>
/// <remarks>
/// <para>
/// QCV8001 的回放時間軸以 `colorMap` 把事件型別分色（見 `docs/PLAYBACK-ALIGNMENT.md`）；
/// 我們的事件刻度長期是白色 1px——操作員看到一個白點，必須點進清單才知道那是警報、
/// 動作、還是攝影機離線。取證時是「沿著時間軸掃」的動作，顏色分不出類別就等於沒有刻度。
/// </para>
/// <para>
/// 分組刻意比類別少（11 類 → 6 色）：顏色要能背，多到十幾種就只是在畫彩虹。
/// 分組沿用 <see cref="EventTaxonomy.CategoryOf"/> 的分類，不另寫一套判斷——
/// 兩套分類遲早會對同一個事件給出不同答案，而那種不一致沒有任何日誌。
/// </para>
/// <para>
/// 與 <see cref="WallModeDotPalette"/> 的關係：那個是「錄影<b>模式</b>」（紅＝全時、藍＝排程…），
/// 這裡是「事件<b>類別</b>」。兩者都在畫面上出現，但回答的是不同問題，刻意不共用色票。
/// </para>
/// </remarks>
public static class TimelineEventPalette
{
    /// <summary>警報／入侵類（感測器、越線、周界）：紅。</summary>
    public const string Alarm = "#E53935";

    /// <summary>動作類（動作偵測、智慧動作）：橘。</summary>
    public const string Motion = "#FB8C00";

    /// <summary>遮蔽／破壞類：紫。</summary>
    public const string Shelter = "#8E24AA";

    /// <summary>網路類（離線／上線）：藍。</summary>
    public const string Network = "#1E88E5";

    /// <summary>人工與排程類：綠。</summary>
    public const string ManualOrSchedule = "#43A047";

    /// <summary>系統與未知類（含查不到的分類）：灰——不是隱形，也不是警告色。</summary>
    public const string System = "#90A4AE";

    /// <summary>事件型別 → 顏色（<see cref="EventTaxonomy.CategoryOf"/> 之後再分組）。</summary>
    public static string For(string? eventType) => GroupOf(EventTaxonomy.CategoryOf(eventType));

    /// <summary>分類 → 顏色；未知分類回 <see cref="System"/>。</summary>
    public static string GroupOf(string? category) => category switch
    {
        EventTaxonomy.Sensor or EventTaxonomy.Tripwire or EventTaxonomy.Perimeter => Alarm,
        EventTaxonomy.Motion or EventTaxonomy.SmartMotion => Motion,
        EventTaxonomy.Shelter => Shelter,
        EventTaxonomy.Offline or EventTaxonomy.Online => Network,
        EventTaxonomy.Manual or EventTaxonomy.Schedule => ManualOrSchedule,
        _ => System,
    };

    /// <summary>全部色值（供快取與測試列舉；順序固定）。</summary>
    public static IReadOnlyList<string> All { get; } =
        [Alarm, Motion, Shelter, Network, ManualOrSchedule, System];
}
