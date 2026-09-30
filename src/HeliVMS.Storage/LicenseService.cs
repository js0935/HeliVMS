using HeliVMS.Licensing;

namespace HeliVMS.Storage;

/// <summary>產品端授權結論（§19.4 驗證時機）。</summary>
public enum LicenseDecision
{
    /// <summary>有效。</summary>
    Valid,

    /// <summary>已到期：停止新增錄影，既有錄影仍可回放（§19.4 不可勒索客戶）。</summary>
    Expired,

    /// <summary>偵測到時鐘被改回，已停用待校時（§19.4 回流時鐘防護）。</summary>
    TimeRollback,

    /// <summary>未匯入授權（未授權安裝屬正常狀態，不記稽核）。</summary>
    NotPresent,

    /// <summary>格式／簽章無效（竄改或金鑰不符）→ 資安事件，必記稽核。</summary>
    Invalid,

    /// <summary>機器綁定不符 → 必記稽核。</summary>
    MachineMismatch,

    /// <summary>已作廢。</summary>
    Revoked,
}

/// <summary>授權套用／驗證結果。</summary>
/// <param name="Decision">結論。</param>
/// <param name="State">底層驗證結果（<c>NotPresent</c> 時為 null）。</param>
/// <param name="Message">可顯示給使用者的訊息；無問題時為 null。</param>
/// <param name="LicenseRowId"><c>license</c> 表列 ID；未寫入時為 null。</param>
/// <param name="Record">寫入之授權列；未寫入時為 null。</param>
public sealed record LicenseApplyResult(
    LicenseDecision Decision,
    LicenseState? State,
    string? Message,
    long? LicenseRowId = null,
    LicenseRecord? Record = null)
{
    /// <summary>是否允許新增錄影（到期與時鐘回流僅停新增，既有檔仍可回放）。</summary>
    public bool AllowsNewRecording => Decision == LicenseDecision.Valid;

    /// <summary>授權通道上限；無有效授權時為 0。</summary>
    public int MaxCameras => Record?.MaxCameras ?? 0;

    /// <summary>授權的功能旗標；無有效授權時為空集合。</summary>
    public IReadOnlyList<string> Features => Record?.Features ?? [];

    /// <summary>
    /// 是否允許使用某功能旗標。授權有效且旗標在授權清單內才為 true；
    /// 僅在已驗證的授權列上判斷，未匯入授權一律 false（§19.9）。
    /// </summary>
    public bool AllowsFeature(string feature)
        => Decision == LicenseDecision.Valid
            && !string.IsNullOrWhiteSpace(feature)
            && Record is not null
            && Record.Features.Contains(feature, StringComparer.Ordinal);
}

/// <summary>
/// 產品端授權整合點（§19.4）：把 <see cref="LicenseManager"/> 的驗證結果落到
/// <c>license</c> 表與稽核日誌，並維護回流時鐘防護的高水位 <c>license.max_seen_dt</c>。
/// <para>桌面端（設定中心匯入授權）與遠端 WebApi 共用本服務，確保遠端無法繞過本地端的
/// 授權狀態判斷（§18.4）。</para>
/// </summary>
public sealed class LicenseService
{
    /// <summary>回流時鐘防護的高水位時間戳記鍵（§19.4 <c>license_max_seen_dt</c>）。</summary>
    public const string MaxSeenSettingKey = "license.max_seen_dt";

    /// <summary>允許的時鐘回退幅度：超過即判定時間被改回（§19.4「7 天內偵測」）。</summary>
    public static readonly TimeSpan RollbackTolerance = TimeSpan.FromDays(7);

    private readonly SettingsRepository _settings;
    private readonly LicenseRepository _licenses;
    private readonly AuditLogRepository _audit;
    private readonly LicenseManager _manager;

    public LicenseService(SqliteStore store)
        : this(store, new LicenseManager())
    {
    }

    public LicenseService(SqliteStore store, LicenseManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _settings = new SettingsRepository(store);
        _licenses = new LicenseRepository(store);
        _audit = new AuditLogRepository(store);
        _manager = manager;
    }

    /// <summary>本機 32 碼設備碼（§19.1），寫入 <c>license.device_code</c> 用。</summary>
    public string DeviceCode => MachineIdProvider.GetDeviceCode();

    /// <summary>
    /// 驗證並套用授權碼：驗證簽章與機器綁定 → 回流時鐘檢查 → 寫入 <c>license</c> 表 → 稽核。
    /// 授權啟用／到期／改版／時鐘回流皆留稽核（§19.8）。
    /// </summary>
    public LicenseApplyResult Apply(string token, string actor, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var state = _manager.Validate(token, nowUtc);
        var deviceCode = DeviceCode;
        var existing = _licenses.GetByDeviceCode(deviceCode);
        var maxSeen = ReadMaxSeenUtc();

        if (state.Status == LicenseStatus.NotPresent)
        {
            // 未授權安裝屬正常狀態，不寫稽核（否則每次啟動都會產生雜訊）。
            return new LicenseApplyResult(LicenseDecision.NotPresent, state, null);
        }

        if (state.Status == LicenseStatus.Invalid || state.Status == LicenseStatus.Tampered)
        {
            AuditSecurity("license.reject", actor, existing, state, nowUtc);
            return new LicenseApplyResult(LicenseDecision.Invalid, state, state.Message);
        }

        if (state.Status == LicenseStatus.MachineMismatch)
        {
            AuditSecurity("license.mismatch", actor, existing, state, nowUtc);
            return new LicenseApplyResult(LicenseDecision.MachineMismatch, state, state.Message);
        }

        if (existing is { Status: LicenseStatuses.Revoked })
        {
            return new LicenseApplyResult(
                LicenseDecision.Revoked,
                state,
                "此授權已作廢，請聯絡原廠重新核發。",
                existing.Id,
                existing);
        }

        var payload = state.Payload!;
        var tier = LicenseTiers.Match(payload.Features, payload.Cameras)?.Name;
        var expires = payload.ExpiresUtc;

        // 回流時鐘防護（§19.4）：只認「現在時間比本機曾見過的最大時間早超過容忍值」。
        // 高水位僅在**驗證成功**時推進（首次啟用與每次成功驗證，§19.4），故
        // 「長期已到期的舊授權」不會被誤判為時鐘回流——舊授權的 max_seen 停在最後一次
        // 有效驗證，now 仍在其之後。
        if (TryDetectRollback(maxSeen, nowUtc, out var rollbackReason))
        {
            var id = _licenses.Upsert(
                token, deviceCode, payload.Id, tier, payload.Cameras, payload.Features, payload.Issuer,
                expires, LicenseStatuses.TimeRollback, actor, nowUtc, markVerified: false);
            _licenses.SetStatus(
                id, LicenseStatuses.TimeRollback, actor, nowUtc,
                $"{rollbackReason}；請校正系統時鐘後重新驗證（回退容忍上限 {RollbackTolerance.TotalDays:0} 天）");
            return new LicenseApplyResult(
                LicenseDecision.TimeRollback,
                state,
                $"系統時鐘疑似被改回（{rollbackReason}），授權已停用。請校正系統時間後重新驗證。",
                id,
                _licenses.Get(id));
        }

        var status = state.Status == LicenseStatus.Expired
            ? LicenseStatuses.Expired
            : LicenseStatuses.Active;
        var message = state.Status == LicenseStatus.Expired ? state.Message : null;

        var rowId = _licenses.Upsert(
            token, deviceCode, payload.Id, tier, payload.Cameras, payload.Features, payload.Issuer,
            expires, status, actor, nowUtc);

        if (state.IsValid)
        {
            WriteMaxSeenUtc(nowUtc, maxSeen);
        }

        if (status == LicenseStatuses.Expired && existing?.Status != LicenseStatuses.Expired)
        {
            // 只在狀態「轉為」到期時記一次，避免每次啟動重複寫入。
            _audit.Record(
                actor,
                "license.expire",
                AuditCategories.License,
                targetType: "license",
                targetId: rowId,
                detail: $"到期 {SqliteStore.Iso(expires!.Value)}；停止新增錄影，既有錄影仍可回放",
                occurredAtUtc: nowUtc);
        }

        return new LicenseApplyResult(
            state.Status == LicenseStatus.Expired ? LicenseDecision.Expired : LicenseDecision.Valid,
            state,
            message,
            rowId,
            _licenses.Get(rowId));
    }

    /// <summary>啟動時驗證既有授權檔（<see cref="LicenseManager.DefaultPath"/>）。</summary>
    public LicenseApplyResult RefreshDefault(string actor, DateTime nowUtc)
        => Apply(ReadDefaultToken(), actor, nowUtc);

    /// <summary>目前資料庫中的授權列（無則 null）。</summary>
    public LicenseRecord? Current() => _licenses.GetByDeviceCode(DeviceCode);

    /// <summary>曾見過的最大時間（<c>license.max_seen_dt</c>；無則 null）。</summary>
    public DateTime? MaxSeenUtc() => ReadMaxSeenUtc();

    private static string ReadDefaultToken()
    {
        var path = LicenseManager.DefaultPath;
        return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
    }

    private static bool TryDetectRollback(DateTime? maxSeenUtc, DateTime nowUtc, out string reason)
    {
        reason = string.Empty;
        if (maxSeenUtc is not { } maxSeen)
        {
            return false;
        }

        if (nowUtc < maxSeen - RollbackTolerance)
        {
            reason = $"現在時間比曾見過的最大時間早 {(maxSeen - nowUtc).TotalDays:0.#} 天";
            return true;
        }

        return false;
    }

    private DateTime? ReadMaxSeenUtc()
    {
        var raw = _settings.Get(MaxSeenSettingKey);
        return string.IsNullOrWhiteSpace(raw) ? null : SqliteStore.FromIso(raw);
    }

    /// <summary>高水位只增不減：時鐘被改回時不得把已記錄的最大時間往後推。</summary>
    private void WriteMaxSeenUtc(DateTime nowUtc, DateTime? current)
    {
        if (current is { } c && c >= nowUtc)
        {
            return;
        }

        _settings.Set(MaxSeenSettingKey, SqliteStore.Iso(nowUtc));
    }

    private void AuditSecurity(
        string action,
        string actor,
        LicenseRecord? existing,
        LicenseState state,
        DateTime nowUtc)
    {
        var deviceCode = DeviceCode;
        _audit.Record(
            actor,
            action,
            AuditCategories.License,
            targetType: "license",
            targetId: existing?.Id,
            detail: $"本機={deviceCode} 授權={state.Payload?.Id ?? "無"} 結果={state.Status}：{state.Message}",
            occurredAtUtc: nowUtc);
    }
}
