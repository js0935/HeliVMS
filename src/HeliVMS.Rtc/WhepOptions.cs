namespace HeliVMS.Rtc;

/// <summary>
/// M244 即時監看的設定（§14.7 #1）。全部以環境變數覆寫，預設值以本機／同網段
/// 遠程監看為前提。
/// <para>
/// 刻意<b>不</b>依賴 <c>IConfiguration</c>：本檔案庫只收純值，組態解析交給
/// WebApi（見 <c>WhepOptionsBinder</c>）。這讓設定解析能在不拉進
/// Microsoft.Extensions 的前提下直接單元測試，也符合 repo 既有的分層慣例。
/// </para>
/// </summary>
public sealed class WhepOptions
{
    /// <summary>環境變數前綴，與 repo 既有 <c>HELIVMS_*</c> 慣例一致。</summary>
    public const string Prefix = "HELIVMS_WHEP_";

    /// <summary>STUN 伺服器清單（逗號分隔）；空白表示只用主機候選。</summary>
    public string[] StunServers { get; init; } = [];

    /// <summary>
    /// 對外廣告的 ICE 候選主機名稱。NAT 後的主機若不設定，瀏覽器拿到的是
    /// 內網位址而連不上——這是「WebRTC 在對稱 NAT 下連不上」最常見的原因。
    /// </summary>
    public string? PublicHost { get; init; }

    /// <summary>對外廣告的 ICE 候選埠；<c>null</c> 代表用實際 listen 埠。</summary>
    public int? PublicPort { get; init; }

    /// <summary>同一通道同時觀看人數上限；超過回 429。</summary>
    public int MaxViewersPerChannel { get; init; } = 8;

    /// <summary>沒有觀看者時，publisher 進程保留多久才關閉。</summary>
    public TimeSpan PublisherIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>WHEP 會話在沒有 DELETE、也沒有收到任何封包時的回收時間。</summary>
    public TimeSpan SessionIdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>publisher 啟動後等待第一個 RTP 封包的逾時；逾時視為啟動失敗。</summary>
    public TimeSpan PublisherStartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>ffmpeg 執行檔路徑。</summary>
    public string FfmpegPath { get; init; } = "ffmpeg";

    /// <summary>
    /// 由「以 key 查值的委派」建立設定，容忍格式錯誤並退回預設。
    /// </summary>
    /// <remarks>
    /// 一律「退回預���」而非「啟動失敗」：這是監看功能，不該因為一個上限值打錯
    /// 就讓整套 NVR 開不起機。但<b>格式錯誤必須記錄</b>，否則維運會以為自己設了
    /// 100 個觀看者上限——由呼叫端（WebApi）負責記錄。
    /// </remarks>
    public static WhepOptions FromLookup(Func<string, string?> lookup, Action<string, string>? onInvalid = null)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        string? Read(string key) => NullIfBlank(lookup(Prefix + key));

        var stun = Read("STUN");
        return new WhepOptions
        {
            StunServers = stun is null
                ? []
                : stun.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            PublicHost = Read("PUBLIC_HOST"),
            PublicPort = ParsePort(Read("PUBLIC_PORT"), onInvalid),
            MaxViewersPerChannel = ParseInt(Read("MAX_VIEWERS"), 8, min: 1, max: 64, name: "MAX_VIEWERS", onInvalid: onInvalid),
            PublisherIdleTimeout = ParseSeconds(Read("PUBLISHER_IDLE_SECONDS"), 30, name: "PUBLISHER_IDLE_SECONDS", onInvalid: onInvalid),
            SessionIdleTimeout = ParseSeconds(Read("SESSION_IDLE_SECONDS"), 30, name: "SESSION_IDLE_SECONDS", onInvalid: onInvalid),
            PublisherStartTimeout = ParseSeconds(Read("PUBLISHER_START_SECONDS"), 30, name: "PUBLISHER_START_SECONDS", onInvalid: onInvalid),
            FfmpegPath = Read("FFMPEG") ?? "ffmpeg",
        };
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? ParsePort(string? value, Action<string, string>? onInvalid)
    {
        if (value is null) return null;
        return int.TryParse(value, out var port) && port is > 0 and <= 65535
            ? port
            : Report<int?>($"HELIVMS_WHEP_PUBLIC_PORT「{value}」不是 1-65535 的埠，已忽略", onInvalid);
    }

    private static int ParseInt(string? value, int fallback, int min, int? max, string name, Action<string, string>? onInvalid)
    {
        if (value is null) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed < min)
        {
            Report<int>($"HELIVMS_WHEP_{name}「{value}」無效（需 >= {min}），已改用 {fallback}", onInvalid);
            return fallback;
        }
        if (max is { } ceiling && parsed > ceiling)
        {
            Report<int>($"HELIVMS_WHEP_{name}「{parsed}」超過合理上限 {ceiling}，已改用 {ceiling}", onInvalid);
            return ceiling;
        }
        return parsed;
    }

    private static TimeSpan ParseSeconds(string? value, int fallback, string name, Action<string, string>? onInvalid)
    {
        if (value is null) return TimeSpan.FromSeconds(fallback);
        if (!int.TryParse(value, out var seconds) || seconds < 1)
        {
            Report<int>($"HELIVMS_WHEP_{name}「{value}」無效（需 >= 1 秒），已改用 {fallback}", onInvalid);
            return TimeSpan.FromSeconds(fallback);
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private static T Report<T>(string message, Action<string, string>? onInvalid)
    {
        onInvalid?.Invoke("HELIVMS_WHEP", message);
        return default!;
    }
}
