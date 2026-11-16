namespace HeliVMS.Shared;

/// <summary>
/// 內嵌 Web 主機（Kestrel）的監聽網址——單一來源。
/// </summary>
/// <remarks>
/// <para>
/// 共用埠模式（QCTek 相容服務啟用）下，公開埠要留給 <c>QctekPortMux</c>，
/// Kestrel 必須改聽系統指派的臨時埠。但 Kestrel <b>不支援 <c>localhost:0</c></b>：
/// 它會拋 <c>InvalidOperationException: Dynamic port binding is not supported when
/// binding to localhost. You must either bind to 127.0.0.1:0 or [::1]:0, or both.</c>
/// </para>
/// <para>
/// 這不是理論問題：那句話被 WebHost 的啟動失敗處理吞掉之後，症狀是
/// 「Web 服務沒起來 → 多工器沒有公開埠可綁 → 只剩 5170 的獨立 listener」，
/// 而 QCC 只會連 Web 埠，於是永遠「連線中斷」，三份日誌裡只有一行看不出關聯的
/// 啟動失敗。所以共用埠時要綁<b>明確的迴環位址</b>，不能寫 <c>localhost</c>。
/// </para>
/// </remarks>
public static class WebListenUrl
{
    /// <summary>共用埠時的監聽網址：明確的 IPv4 迴環位址 + 系統指派埠。</summary>
    public const string DynamicLoopback = "http://127.0.0.1:0";

    /// <summary>只聽迴圈位址的監聽網址前綴（預設；見 <see cref="WebBindPolicy"/> 的說明）。</summary>
    public const string LoopbackPrefix = "http://localhost";

    /// <summary>聽所有介面的監聽網址前綴（操作員明講要開放區域網路時）。</summary>
    public const string AllInterfacesPrefix = "http://*";

    /// <summary>
    /// 組出 Kestrel 的 <c>--urls</c>。
    /// </summary>
    /// <param name="port">對外（操作員設定）的公開埠。</param>
    /// <param name="sharePort">
    /// <c>true</c>＝公開埠由前置多工器擁有，這裡只綁臨時埠，<b>不能帶公開埠號</b>。
    /// </param>
    /// <param name="allowLan">
    /// <c>true</c>＝綁所有介面（辦公室／手機連得進來）。
    /// 是否允許由 <see cref="WebBindPolicy.Resolve"/> 決定，這裡只負責把決定翻成監聽網址：
    /// 預設<b>只綁迴圈位址</b>，因為這個介面沒有 TLS 且可以看到影像、操作 PTZ、匯出錄影。
    /// </param>
    public static string For(int port, bool sharePort, bool allowLan = false)
        => sharePort
            ? DynamicLoopback
            : allowLan
                ? $"{AllInterfacesPrefix}:{port}"
                : $"{LoopbackPrefix}:{port}";
}
