namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// 相容服務的連接埠計畫（純值）：Web 埠要不要由多工器接手，以及要不要另外開一個
/// 獨立埠給「在 CMS 裡明確填了埠」的遠端站台。
/// </summary>
/// <param name="ShareWebPort">
/// <c>true</c>＝公開的 Web 埠由 <see cref="QctekPortMux"/> 擁有（Kestrel 改聽臨時埠）。
/// </param>
/// <param name="PublicPort">多工器要綁的公開埠（= Web 埠）；不共用時為 <c>0</c>。</param>
/// <param name="StandalonePort">獨立 <see cref="NemoServer"/> 的埠；不需要時為 <c>0</c>。</param>
public readonly record struct QctekPortPlan(bool ShareWebPort, int PublicPort, int StandalonePort)
{
    /// <summary>有沒有任何一個 listener 要開。</summary>
    public bool Anything => ShareWebPort || StandalonePort > 0;
}

/// <summary>
/// 「Web 服務與 QCTek 相容服務要怎麼分配連接埠」的決策（純函式）。
/// </summary>
/// <remarks>
/// <para>
/// 關鍵事實：<b>QCC 沒有地方可以填「NEMO 埠」</b>——加裝置時它只知道裝置位址（埠固定走
/// Web 埠，真機 QCV8001 把 NEMO 與 HTTP 多工在 80）。所以「相容服務啟用」就等於
/// 「Web 埠上要有人聽得懂 NEMO」，與 <c>qctek.compat.port</c> 填什麼無關。
/// </para>
/// <para>
/// 這也是實測踩過的坑：<c>web.port=80</c> 而 <c>qctek.compat.port=5170</c> 時，
/// 舊規則認為「兩個埠不同 → 不需要多工器」，於是 80 只有 Kestrel，QCC 送出的 NEMO
/// 命令換到一句 <c>400 Bad Request</c>：裝置加得進去，畫面永遠「連線中斷」，日誌乾淨。
/// </para>
/// <para>
/// <c>qctek.compat.port</c> 因此只服務「在 CMS 端明確指定了這個埠」的遠端站台：填的埠與
/// Web 埠<b>不同</b>時，兩個 listener 同時存在（多工器綁 loopback 的 Web 埠、NemoServer
/// 綁所有介面的相容埠）；<b>相同</b>時只留多工器，不要在同一個埠上開第二個 listener。
/// </para>
/// <para>
/// 為什麼抽成純函式：這個判斷同時決定啟動／停止的<b>順序</b>——共用時 Web 要先改聽
/// 臨時埠、多工器才綁公開埠；不共用時多工器要先放掉公開埠、Web 才能綁回去。
/// 順序反了就是「連接埠已被占用」，而設定頁只會顯示一句啟動失敗。
/// </para>
/// </remarks>
public static class QctekPortPlanner
{
    /// <summary>依設定算出要開哪些 listener。</summary>
    public static QctekPortPlan Plan(
        bool webEnabled,
        bool compatibilityEnabled,
        int webPort,
        int compatibilityPort)
    {
        if (!compatibilityEnabled || !IsPort(compatibilityPort))
        {
            // 相容服務沒開（或埠壞掉）：完全沒有 NEMO 要應答，Web 服務不必讓位。
            return default;
        }

        if (!webEnabled || !IsPort(webPort))
        {
            // Web 服務沒開（或埠壞掉）：只有獨立 listener，沒有公開埠可以搶。
            return new QctekPortPlan(ShareWebPort: false, PublicPort: 0, StandalonePort: compatibilityPort);
        }

        return webPort == compatibilityPort
            ? new QctekPortPlan(ShareWebPort: true, PublicPort: webPort, StandalonePort: 0)
            : new QctekPortPlan(ShareWebPort: true, PublicPort: webPort, StandalonePort: compatibilityPort);
    }

    /// <summary>
    /// 只開獨立 listener 的計畫（沒有協調者、Web 不在本行程，或退回舊模式時使用）。
    /// </summary>
    public static QctekPortPlan Standalone(bool compatibilityEnabled, int compatibilityPort)
        => compatibilityEnabled && IsPort(compatibilityPort)
            ? new QctekPortPlan(ShareWebPort: false, PublicPort: 0, StandalonePort: compatibilityPort)
            : default;

    /// <summary>設定頁會擋掉這些值，但索引可以直接被改壞或從舊版升級而來。</summary>
    public static bool IsPort(int port) => port is > 0 and <= 65535;
}
