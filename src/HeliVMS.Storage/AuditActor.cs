namespace HeliVMS.Storage;

/// <summary>
/// 目前操作者（環境值，M278，§7.6 稽核）。桌面程式在登入成功後由 <c>SessionContext</c>
/// 設為登入帳號；未登入（auth 未啟用）、背景/系統工作（匯出佇列、備份、保留清理）與
/// WebApi 程序則維持預設 <see cref="System"/>。
///
/// <para>
/// 為什麼要這個：管理面倉儲的 <c>actor</c> 參數原先一律預設字面值 <c>"system"</c>，
/// 於是稽核只記得到「有人改了」，記不到「誰改的」——大型案場要回答「誰停用了這台攝影機」
/// 時派不上用場。改為：倉儲的 <c>actor</c> 預設 <c>null</c>，由
/// <see cref="AuditLogRepository.Record"/> 在收到 <c>null</c> 時補上此環境值；呼叫端若
/// 明確傳 <c>"system"</c>（背景工作）則以明確值為準，不會被誤記成登入者。
/// </para>
/// </summary>
public static class AuditActor
{
    /// <summary>系統/背景工作的操作者名稱。</summary>
    public const string System = "system";

    private static volatile string _current = System;

    /// <summary>目前操作者；未設定時為 <see cref="System"/>。</summary>
    public static string Current => _current;

    /// <summary>設定目前操作者；<c>null</c>／空白視為 <see cref="System"/>。</summary>
    public static void Set(string? actor)
        => _current = string.IsNullOrWhiteSpace(actor) ? System : actor.Trim();

    /// <summary>還原為 <see cref="System"/>（登出、測試收尾）。</summary>
    public static void Reset() => _current = System;
}
