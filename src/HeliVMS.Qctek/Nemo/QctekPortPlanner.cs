namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// 「Web 服務與 QCTek 相容服務要不要共用同一個埠」的決策（純函式）。
/// </summary>
/// <remarks>
/// <para>
/// QCC 連的是裝置的 Web 埠；兩個服務的埠<b>相同</b>時才需要用
/// <see cref="QctekPortMux"/> 多工，埠不同則相容服務照舊用獨立埠
/// （<see cref="NemoServer"/>），Web 服務不必讓出公開埠。
/// </para>
/// <para>
/// 為什麼抽成純函式：這個判斷同時決定啟動／停止的<b>順序</b>——共用時 Web 要先改聽
/// 臨時埠、多工器才綁公開埠；不共用時多工器要先放掉公開埠、Web 才能綁回去。
/// 順序反了就是「連接埠已被占用」，而設定頁只會顯示一句啟動失敗。
/// </para>
/// </remarks>
public static class QctekPortPlanner
{
    /// <summary>
    /// 兩個服務都啟用且指定同一個埠時，才由前置多工器擁有該埠。
    /// </summary>
    public static bool ShouldSharePort(
        bool webEnabled,
        bool compatibilityEnabled,
        int webPort,
        int compatibilityPort)
        => webEnabled
           && compatibilityEnabled
           && webPort is > 0 and <= 65535
           && webPort == compatibilityPort;
}
