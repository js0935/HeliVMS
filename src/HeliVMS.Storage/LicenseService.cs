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
/// <param name="State">底層驗證結果（<c>NotPresent</c> 或唯讀 <c>Evaluate</c> 時為 null）。</param>
/// <param name="Message">可顯示給使用者的訊息；無問題時為 null。</param>
/// <param name="LicenseRowId"><c>license</c> 表列 ID；未寫入時為 null。</param>
/// <param name="Record">授權列；未寫入時為 null。</param>
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

    /// <summary>到期提醒（M211／§19.4「到期前 14 天 UI 浮條提醒」）。</summary>
    /// <remarks>
    /// 已到期（<see cref="LicenseDecision.Expired"/>）**要**提醒——續期訊息正是到期後才最需要的；
    /// 未匯入／作廢／時鐘回流／簽章無效則不提醒，那四種是「授權無效」而非「快到期」。
    /// </remarks>
    public LicenseExpiryNotice Expiry(DateTime nowUtc)
        => LicenseExpiry.Evaluate(
            Record?.ExpiresUtc,
            nowUtc,
            Decision is LicenseDecision.Valid or LicenseDecision.Expired);

    /// <summary>到期時刻；永久授權或無授權列時為 null。</summary>
    public DateTime? ExpiresUtc => Record?.ExpiresUtc;

    /// <summary>
    /// 是否允許使用某功能旗標。授權有效且旗標在授權清單內才為 true；
    /// 未匯入／到期／回流／作廢一律 false（§19.4 fail-closed）。
    /// </summary>
    public bool AllowsFeature(string feature)
        => Decision == LicenseDecision.Valid
            && !string.IsNullOrWhiteSpace(feature)
            && Record is not null
            && Record.Features.Contains(feature, StringComparer.Ordinal);

    /// <summary>
    /// 某功能不可用時的使用者可讀說明（桌面端提示、CLI 訊息、WebApi 錯誤共用）。
    /// 可用時回傳 null。
    /// </summary>
    public string? FeatureDenialMessage(string feature)
    {
        if (AllowsFeature(feature))
        {
            return null;
        }

        var name = LicenseFeatures.DisplayName(feature);
        return Decision switch
        {
            LicenseDecision.NotPresent => $"{LicenseService.NotPresentMessage}（缺少功能：{name}）",
            LicenseDecision.Revoked => $"{LicenseService.RevokedMessage}（缺少功能：{name}）",
            _ => $"目前授權未包含「{name}」，此功能已停用。{Message}",
        };
    }
}

/// <summary>單一頻道的錄影閘門結論（§19.4）。</summary>
/// <param name="Allowed">是否可**新增**錄影。</param>
/// <param name="Decision">造成阻擋的授權結論（<c>Valid</c> 表示無阻擋）。</param>
/// <param name="MaxCameras">授權通道上限；未匯入授權時為 0。</param>
/// <param name="Reason">可顯示給使用者的阻擋原因；允許時為 null。</param>
/// <param name="ChannelId">被判定的頻道 ID。</param>
public sealed record RecordingGateResult(
    bool Allowed,
    LicenseDecision Decision,
    int MaxCameras,
    string? Reason,
    int ChannelId);

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
    private readonly HashSet<string> _auditedGateRejections = new(StringComparer.Ordinal);
    private readonly Lock _gateAuditLock = new();

    /// <summary>未匯入授權時的統一說明（§19.4 錄影核心啟動失敗 → 拒絕錄影）。</summary>
    public const string NotPresentMessage =
        "未匯入授權，已停止新增錄影；既有錄影仍可回放。請於設定中心匯入授權碼。";

    /// <summary>授權已作廢時的統一說明。</summary>
    public const string RevokedMessage = "此授權已作廢，請聯絡原廠重新核發。";

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
                RevokedMessage,
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

    /// <summary>
    /// 啟動時重新驗證既有授權（M213，§19.4「錄影核心啟動：失敗 → 拒絕錄影」）。
    /// </summary>
    /// <remarks>
    /// <para><b>兩個來源都要看</b>：<c>license.lic</c> 檔與 <c>license</c> 列各自都是
    /// 可能的竄改目標，而列的 <c>features</c>／<c>max_cameras</c> 正是錄影閘門唯一的判斷依據。
    /// 因此以 <see cref="LicenseManager.Validate"/> 逐一驗證兩者，取可驗證且較新的一張套用；
    /// 若**兩個來源都無法驗證**，把授權列標記為 <see cref="LicenseStatuses.Invalid"/>——
    /// 只記稽核卻留著有效的列，等於沒有任何防護。</para>
    /// <para>套用走的是與手動匯入完全相同的 <see cref="Apply"/> 路徑，故稽核（啟用／改版／
    /// 到期／時鐘回流）與 <c>last_verified</c> 自動一致，不會出現兩套行為。</para>
    /// <para>未匯入授權的機器不會產生任何稽核雜訊（<see cref="LicenseApplyResult"/> 回
    /// <see cref="LicenseDecision.NotPresent"/>），這是正常安裝狀態而非錯誤。</para>
    /// </remarks>
    /// <param name="actor">稽核操作者（一般為 <c>startup</c>）。</param>
    /// <param name="nowUtc">判斷時點。</param>
    public LicenseApplyResult RefreshDefault(string actor, DateTime nowUtc)
        => Refresh(ReadDefaultToken(), actor, nowUtc);

    /// <summary>
    /// <see cref="RefreshDefault"/> 的核心：以明確提供的「授權檔內容」重新驗證。
    /// 抽出來是為了讓測試不必碰使用者實際的 <c>%LOCALAPPDATA%\HeliVMS\license.lic</c>——
    /// 讀真檔的測試會在開發者機器上時靈時不靈，而且可能蓋掉人家的授權檔。
    /// </summary>
    /// <param name="fileToken">授權檔內容（可為空字串表示沒有檔案）。</param>
    /// <param name="actor">稽核操作者。</param>
    /// <param name="nowUtc">判斷時點。</param>
    public LicenseApplyResult Refresh(string fileToken, string actor, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        fileToken ??= string.Empty;

        var row = Current();

        var candidates = new List<(LicenseState State, string Token)>();
        var fileState = _manager.Validate(fileToken, nowUtc);
        if (IsTrustworthy(fileState))
        {
            candidates.Add((fileState, fileToken));
        }

        if (row is { KeyText.Length: > 0 } && !string.Equals(row.KeyText, fileToken, StringComparison.Ordinal))
        {
            var rowState = _manager.Validate(row.KeyText, nowUtc);
            if (IsTrustworthy(rowState))
            {
                candidates.Add((rowState, row.KeyText));
            }
        }

        if (candidates.Count == 0)
        {
            return FailRefresh(row, fileToken, actor, nowUtc);
        }

        // 取較新的一張：實務上廠商換發授權後只會更新其中一個來源，
        // 取最新才能讓「換發」在下一次啟動就生效。
        var winner = candidates.MaxBy(c => c.State.Payload!.IssuedUtc);
        return Apply(winner.Token, actor, nowUtc);
    }

    /// <summary>驗證結果是否可作為信任來源（簽章與機器綁定都過）。</summary>
    private static bool IsTrustworthy(LicenseState state)
        => state.Status is LicenseStatus.Valid or LicenseStatus.Expired;

    /// <summary>
    /// 兩個來源都無法驗證：把授權列標記失效並回報原因。
    /// 已作廢者維持原狀——作廢是廠商意志，不能被一次檔案遺失蓋掉。
    /// </summary>
    private LicenseApplyResult FailRefresh(
        LicenseRecord? row,
        string fileToken,
        string actor,
        DateTime nowUtc)
    {
        var reason = row is null
            ? (fileToken.Length > 0 ? "授權檔內容無法驗證" : NotPresentMessage)
            : "授權列與授權檔皆無法通過簽章或機器綁定驗證";
        var rejected = new LicenseState(LicenseStatus.Invalid, null, reason);

        if (row is not null && row.Status is not (LicenseStatuses.Revoked or LicenseStatuses.Invalid))
        {
            _licenses.SetStatus(row.Id, LicenseStatuses.Invalid, actor, nowUtc, reason);
            AuditSecurity("license.reject", actor, row, rejected, nowUtc);
        }

        if (row is null && fileToken.Length > 0)
        {
            AuditSecurity("license.reject", actor, null, rejected, nowUtc);
        }

        return new LicenseApplyResult(
            row is null && fileToken.Length == 0 ? LicenseDecision.NotPresent : LicenseDecision.Invalid,
            null,
            reason,
            row?.Id,
            _licenses.Get(row?.Id ?? 0));
    }

    /// <summary>
    /// 唯讀評估目前授權狀態：<b>不寫 <c>license</c> 表、不記稽核、不做 RSA 驗證</b>。
    ///
    /// 供錄影閘門等高频路徑使用（M208）。授權列本身是某次 <see cref="Apply"/>（已驗簽章與機器
    /// 綁定）留下的快取，故此處信任該列；真正的竄改嘗試會在下次啟動的 <see cref="RefreshDefault"/>
    /// 被驗簽擋下並改寫此列。若改為每次閘門都呼叫 <see cref="Apply"/>，排程器每 30 秒一次就會
    /// 灌爆稽核日誌。
    /// </summary>
    public LicenseApplyResult Evaluate(DateTime nowUtc)
    {
        var record = Current();

        if (record is null)
        {
            return new LicenseApplyResult(LicenseDecision.NotPresent, null, NotPresentMessage);
        }

        if (record.Status == LicenseStatuses.Revoked)
        {
            return new LicenseApplyResult(
                LicenseDecision.Revoked, null, RevokedMessage, record.Id, record);
        }

        // 已標記 invalid 者（啟動時重新驗證發現竄改）只有重新匯入合法授權才會恢復；
        // Evaluate 絕不自行放行——放行等於讓手改資料庫的人拿到錄影權。
        if (record.Status == LicenseStatuses.Invalid)
        {
            return new LicenseApplyResult(
                LicenseDecision.Invalid,
                null,
                "授權資料已被竄改或毀損，已停止新增錄影。請向原廠重新索取授權金鑰並重新匯入。",
                record.Id,
                record);
        }

        if (TryDetectRollback(ReadMaxSeenUtc(), nowUtc, out var rollbackReason))
        {
            return new LicenseApplyResult(
                LicenseDecision.TimeRollback,
                null,
                $"系統時鐘疑似被改回（{rollbackReason}），已停止新增錄影。請校正系統時間後重新驗證。",
                record.Id,
                record);
        }

        // 已標記 time_rollback 者只有重新驗證（Apply）才會解除，Evaluate 不得自行放行。
        if (record.Status == LicenseStatuses.TimeRollback)
        {
            return new LicenseApplyResult(
                LicenseDecision.TimeRollback,
                null,
                "系統時鐘疑似被改回，授權已停用。請校正系統時間後重新驗證授權。",
                record.Id,
                record);
        }

        if (record.ExpiresUtc is { } expires && expires <= nowUtc)
        {
            return new LicenseApplyResult(
                LicenseDecision.Expired,
                null,
                $"授權已於 {SqliteStore.Iso(expires)} 到期，已停止新增錄影；既有錄影仍可回放。",
                record.Id,
                record);
        }

        return new LicenseApplyResult(LicenseDecision.Valid, null, null, record.Id, record);
    }

    /// <summary>
    /// 錄影閘門（§19.4「超限行為」「到期行為」）：判斷某頻道此刻能否**新增**錄影。
    /// 到期與時鐘回流只停新增，既有錄影檔仍可回放（不可勒索客戶）。
    /// </summary>
    /// <param name="channelId">欲錄影的頻道 ID。</param>
    /// <param name="nowUtc">判斷時點。</param>
    /// <param name="source">觸發來源（<c>manual</c>／<c>schedule</c>），寫入稽核供追查。</param>
    public RecordingGateResult CheckRecording(int channelId, DateTime nowUtc, string source)
    {
        if (channelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelId), "頻道 ID 須為正整數。");
        }

        var license = Evaluate(nowUtc);
        var allowed = true;
        string? reason = null;

        if (!license.AllowsNewRecording)
        {
            allowed = false;
            reason = license.Decision switch
            {
                LicenseDecision.NotPresent => NotPresentMessage,
                LicenseDecision.Revoked => RevokedMessage,
                _ => license.Message,
            };
        }
        else if (channelId > license.MaxCameras)
        {
            // 以頻道 ID 與上限比對而非計算「目前有幾個在錄」：ID 為 AUTOINCREMENT 且不會因
            // 刪除而回收，用它判定不受已刪除頻道與種子頻道（EnsureSeedChannels 預建 2 個）
            // 影響，判定結果只取決於授權本身。
            allowed = false;
            reason = $"已達授權上限（{license.MaxCameras} 路）：頻道 {channelId} 超出授權範圍，" +
                "可瀏覽設備但無法錄影。請升級授權以解鎖更多頻道。";
        }

        if (allowed)
        {
            return new RecordingGateResult(true, LicenseDecision.Valid, license.MaxCameras, null, channelId);
        }

        AuditGateRejection(channelId, reason!, source, nowUtc);
        return new RecordingGateResult(
            false, license.Decision, license.MaxCameras, reason, channelId);
    }

    /// <summary>目前資料庫中的授權列（無則 null）。</summary>
    public LicenseRecord? Current() => _licenses.GetByDeviceCode(DeviceCode);

    /// <summary>曾見過的最大時間（<c>license.max_seen_dt</c>；無則 null）。</summary>
    public DateTime? MaxSeenUtc() => ReadMaxSeenUtc();

    /// <summary>
    /// 同一「頻道＋原因＋來源」在本次程序執行期間只記一次。
    ///
    /// §19.4 要求被拒時登入稽核，但排程器每 30 秒調和一次；若每次都寫，使用者只看得到
    /// 幾萬筆同樣的拒絕紀錄，真正的授權事件反而被埋掉。
    /// </summary>
    private void AuditGateRejection(int channelId, string reason, string source, DateTime nowUtc)
    {
        var key = $"{channelId}|{reason}|{source}";
        lock (_gateAuditLock)
        {
            if (!_auditedGateRejections.Add(key))
            {
                return;
            }
        }

        _audit.Record(
            source,
            "license.recording_blocked",
            AuditCategories.License,
            targetType: "channel",
            targetId: channelId,
            detail: reason,
            occurredAtUtc: nowUtc);
    }

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
