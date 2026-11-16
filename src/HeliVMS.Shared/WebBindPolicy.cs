namespace HeliVMS.Shared;

/// <summary>不開放區域網路的原因（<see cref="WebBindDenial.None"/>＝沒有被擋）。</summary>
public enum WebBindDenial
{
    /// <summary>沒有被擋。</summary>
    None,

    /// <summary>還開著「允許無金鑰存取」——那個組合等於把整台 NVR 的設定、影像與匯出送給整個網段。</summary>
    NoKey,
}

/// <summary>一個「要不要讓區域網路連得到 Web 管理介面」的決定。</summary>
public readonly record struct WebBindDecision(bool AllowLan, WebBindDenial Denial);

/// <summary>
/// 「內嵌 Web 管理介面要不要綁到區域網路」的政策（純函式）。
/// </summary>
/// <remarks>
/// <para>
/// 預設是<b>只綁迴圈位址</b>：這個介面沒有 TLS，而且可以看影像、操作 PTZ、匯出錄影、改設定。
/// 但操作員的實際需求是「在辦公室連進來看」，所以必須有一條明講的、可稽核的路，而不是永遠只能
/// 在本機開（或反過來偷偷綁 0.0.0.0）。
/// </para>
/// <para>
/// 唯一的拒絕理由刻意只有一個：<b>還開著「允許無金鑰存取」</b>。那個組合的後果不是「少一層
/// 驗證」，而是任何能連到這個埠的人都能看畫面、轉鏡頭、下載錄影——比不開放更糟的是「以為有
/// 開但其實全開」。要開放就先關掉它（並建議啟用帳號登入），讓遠端連線至少帶著一個身分。
/// </para>
/// <para>
/// 抽成純函式的原因是它會出現在兩個地方（設定頁的即時判斷、以及真正啟動 Kestrel 的那一段），
/// 兩邊各寫一份條件遲早會漂移——而漂移的方向是「設定頁說可以，實際上沒開」或反過來，兩者都會
/// 讓操作員在錯誤的地方找問題。
/// </para>
/// </remarks>
public static class WebBindPolicy
{
    /// <summary>「允許區域網路存取」的設定鍵（預設 0＝只綁迴圈位址）。</summary>
    public const string AllowLanKey = "web.allow_lan";

    /// <summary>
    /// 「允許無金鑰存取」的設定鍵。政策本體讀它來判斷能不能開放區域網路，
    /// 所以鍵名也放在這裡（<c>WebHost.AllowNoKeyKey</c> 沿用同一個字串）。
    /// </summary>
    public const string NoKeyKey = "web.allow_no_key";

    /// <summary>依「是否要求開放」與「是否還允許無金鑰存取」算出決定。</summary>
    /// <param name="requested">設定頁的「允許區域網路存取」勾選狀態。</param>
    /// <param name="allowNoKey"><c>web.allow_no_key</c> 的原始值（<c>"1"</c>＝允許）。</param>
    public static WebBindDecision Resolve(bool requested, string? allowNoKey)
    {
        if (!requested)
        {
            return new WebBindDecision(AllowLan: false, WebBindDenial.None);
        }

        return allowNoKey == "1"
            ? new WebBindDecision(AllowLan: false, WebBindDenial.NoKey)
            : new WebBindDecision(AllowLan: true, WebBindDenial.None);
    }
}
