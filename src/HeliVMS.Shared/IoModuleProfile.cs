namespace HeliVMS.Shared;

/// <summary>
/// 網路 IO 模組的機型描述：一台模組有幾個 DI、幾個 DO，以及它預設的連線參數。
/// </summary>
/// <remarks>
/// <para>
/// 需要這一層的理由是「新增一顆 ADAM-6250」對操作員來說是<b>一件事</b>——填位址、按下新增，
/// 然後就該有 8 個 DI 與 8 個 DO 出現在 IO 頁面上、可以掛規則、可以放到地圖上。若要求他先
/// 自己算出「這台有幾個點、編號從幾開始」，那他多半會少填幾個點，而症狀是「有一半的接點
/// 看不到」，沒有任何錯誤訊息。
/// </para>
/// <para>
/// 點數<b>可以覆寫</b>：機型表只是預設值。同樣叫 ADAM 的模組有 8DI/8DO、16DI、16DO、8 繼電器
/// 等不同型號，與其讓程式猜（猜錯就是少幾個點），不如讓操作員在表單上改一個數字——但預設值
/// 必須是對的那一個。
/// </para>
/// <para>
/// 這張表刻意只收「一般 Modbus/TCP 模組」的共通部分：DI 讀離散輸入（FC 02）、DO 讀寫線圈
/// （FC 01／05）、兩者位址都從 0 起算。這正是 ADAM 系列與絕大多數模組的對應方式，也是我們
/// Modbus 客戶端已經支援的三個功能碼——不引入第二種協定。
/// </para>
/// </remarks>
public readonly record struct IoModuleProfile(
    string Kind,
    string DisplayName,
    int DefaultDiCount,
    int DefaultDoCount,
    int DefaultPort = 502,
    int DefaultUnitId = 1)
{
    /// <summary>研華 ADAM-6250：8 個數位輸入 ＋ 8 個數位輸出（Modbus/TCP，埠 502）。</summary>
    public static IoModuleProfile Adam6250 { get; } = new("adam6250", "ADAM-6250", 8, 8);

    /// <summary>
    /// 一般 Modbus/TCP 模組：點數由操作員在表單上指定（預設 8／8，與 ADAM-6250 相同）。
    /// </summary>
    public static IoModuleProfile GenericModbus { get; } = new("modbus", "Modbus/TCP 模組", 8, 8);

    /// <summary>表單可選的機型（順序即畫面順序：最常用的在最前面）。</summary>
    public static IReadOnlyList<IoModuleProfile> All { get; } = [Adam6250, GenericModbus];

    /// <summary>依儲存的值取回機型；認不得時回 <see cref="GenericModbus"/>（舊資料不該讓模組打不開）。</summary>
    public static IoModuleProfile FromKind(string? kind)
        => All.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase))
           is { Kind.Length: > 0 } match
            ? match
            : GenericModbus;

    /// <summary>建立這個機型的通道名稱（DI 1…N／DO 1…M）。</summary>
    /// <remarks>
    /// 預設名稱要能一眼看出方向與編號：掛規則時清單裡會有十幾個「未命名」，而「DI 3」至少
    /// 讓操作員能對著模組接線圖找到它。之後可以在 IO 頁面改成「大門磁簧」這種名字。
    /// </remarks>
    public static string ChannelName(string direction, int index)
        => $"{direction} {index + 1}";

    /// <summary>點數的合理上限（一個 Modbus 讀取請求最多 2000 個位元；這裡保守取 64）。</summary>
    public const int MaxChannelCount = 64;

    /// <summary>把表單／API 傳進來的點數夾到合理範圍（&lt;= 0 時用機型預設）。</summary>
    public int ResolveDiCount(int? requested)
        => requested is > 0 ? Math.Min(requested.Value, MaxChannelCount) : DefaultDiCount;

    /// <inheritdoc cref="ResolveDiCount"/>
    public int ResolveDoCount(int? requested)
        => requested is > 0 ? Math.Min(requested.Value, MaxChannelCount) : DefaultDoCount;
}
