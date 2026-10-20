using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;
using Microsoft.Data.Sqlite;

namespace HeliVMS.WebApi;

/// <summary>
/// Command/query surface for the P0 local Web API (M117, section 14.3):
/// channels, paged events, forensic full-text, alarm board/summary + disposition
/// actions, POS query/reconciliation and the smart-wall board feed.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>單次播放清單的時段上限（§14.3 串流）：避免一個請求產生無上限的清單。</summary>
    public static readonly TimeSpan MaxPlaylistWindow = TimeSpan.FromHours(24);

    /// <summary>讀取錄影檔前綴以切出初始化段的上限；ftyp＋moov 通常只有幾 KB。</summary>
    private const int InitPrefixBytes = 4 * 1024 * 1024;

    public sealed record Paged<T>(IReadOnlyList<T> Items, int Count);

    public sealed record HealthResponse(string Status, string Database, int Channels);

    /// <summary>
    /// 中央站回傳的節點狀態（§14.1 #10，M295）。
    /// 刻意不含 <c>ApiKey</c>：列出節點不該把各子節點的金鑰再回吐給呼叫端。
    /// </summary>
    public sealed record CentralNodeStatusResponse(
        long Id,
        string Name,
        string BaseUrl,
        bool Enabled,
        string? NodeId,
        string? Version,
        int? Channels,
        bool? Online,
        string? LastSeenUtc,
        string? LastError);

    /// <summary>中央節點新增（M299／§14.1 #10）：名稱與位址必填，金鑰可空（子節點未開金鑰時）。</summary>
    public sealed record CentralNodeUpsertRequest(string Name, string BaseUrl, string? ApiKey, bool? Enabled);

    /// <summary>中央節點更新（M299）：未提供的欄位保持原值；有提供但空白者視為 400。</summary>
    public sealed record CentralNodeUpdateRequest(string? Name, string? BaseUrl, string? ApiKey, bool? Enabled);

    public sealed record SegmentListItem(long Id, int ChannelId, string Stream, DateTime StartUtc, DateTime? EndUtc, long SizeBytes, double? DurationSec, string Format, SegmentStatus Status, string? Sha256);
    public sealed record AckRequest(bool Acknowledged);
    public sealed record TriageRequest(string Priority, DateTime? DueUtc, string? Owner);
    public sealed record DispositionRequest(string Status, string? AssignedTo, string? Note);
    public sealed record PosReconResult(int Total, int Matched, int Unmatched, int Duplicates);

    /// <summary>POS 交易匯入（M93／§14.7 #8）：金額以分（cents）傳入，避免浮點誤差。</summary>
    public sealed record PosEventRequest(int DeviceId, string RegisterId, string TransactionNo, long AmountCents, DateTime OccurredAtUtc);

    /// <summary>POS 匯入結果：<c>Inserted=false</c> 代表同一交易在去重窗內已存在，回傳既有 Id。</summary>
    public sealed record PosIngestResult(long Id, bool Inserted);

    /// <summary>門禁刷卡事件匯入（M92／§14.7 #8）：direction 為 In／Out（大小寫不拘）。</summary>
    public sealed record DoorEventRequest(int DeviceId, int DoorId, string CardId, string Direction, bool Granted, string? Reason, DateTime OccurredAtUtc);

    /// <summary>門禁匯入結果：<c>Inserted=false</c> 代表同一刷卡在去重窗內已存在，回傳既有 Id。</summary>
    public sealed record DoorEventIngestResult(long Id, bool Inserted);
    public sealed record LoginRequest(string Username, string Password);
    public sealed record AccountUpsertRequest(string Username, string Password, string Role, string? DisplayName);
    public sealed record AccountPatchRequest(string? Role, bool? Enabled, string? DisplayName);
    public sealed record AccountListItem(int Id, string Username, string Role, string? DisplayName, bool Enabled, bool Locked);
    public sealed record ConfigResponse(bool AuthEnabled, int LockoutThreshold, int LockoutMinutes, int RecordingRetentionDays, double RecordingWatermarkGb, int AlarmRetentionDays, int AuditRetentionDays = 365);
    public sealed record ConfigRequest(bool? AuthEnabled, int? LockoutThreshold, int? LockoutMinutes, int? RecordingRetentionDays, double? RecordingWatermarkGb, int? AlarmRetentionDays, int? AuditRetentionDays = null);
    public sealed record UsageResponse(long Bytes, double Gb, int RetentionDays, double WatermarkGb, int AlarmRetentionDays, int AuditRetentionDays = 365);
    public sealed record RetentionRunResult(int AgePurged, int WatermarkPurged, long BytesFreed, long AlarmPurged, int Skipped = 0, long AuditPurged = 0, long AudioPurged = 0);
    public sealed record EvidencePackageRequest(string BundleName, IReadOnlyList<string> Files, string? Password);
    public sealed record EvidenceVerifyRequest(string BundlePath, string? Password);
    public sealed record EvidencePackageResult(string BundlePath, string BundleSha256, int Items, string CreatedUtc);
    public sealed record EvidenceVerifyResult(bool Valid, bool Expired, IReadOnlyList<string> Failures, IReadOnlyList<EvidenceItemBody> Items);
    public sealed record EvidenceItemBody(string RelativePath, string Sha256, long SizeBytes, string Kind);
    public sealed record EvidenceListItem(int Id, string Status, string CreatedAt, string? LastVerifiedAt, int Items);
    public sealed record BackupRunRequest(string SourceRoot, string TargetRoot);
    public sealed record DoorEventItem(long Id, int DeviceId, int DoorId, string CardId, string Direction, bool Granted, string Reason, string OccurredAtUtc);
    public sealed record DetectionItem(long Id, int ChannelId, string Class, float Confidence, float X, float Y, float W, float H, string DetectedUtc);
    public sealed record DetectionSummaryItem(string Class, int Count);
    public sealed record NotificationLogItem(long Id, string TsUtc, int ChannelId, string EventType, string Route, bool Ok, int Attempts, string? Detail);
    public sealed record ExportJobRequest(int ChannelId, string Stream, DateTime FromUtc, DateTime ToUtc);
    public sealed record ExportJobItem(long Id, int ChannelId, string Stream, string StartUtc, string EndUtc, string Status, string? OutputPath, long? FileSizeBytes, string? Sha256, string? Error, string CreatedUtc);
    public sealed record ExportVerifyItem(long Id, bool ReceiptExists, bool HashMatches, bool SignatureValid, bool SignerMatched, bool SelfAssertedKey, string? Signer, bool Valid, string? Detail);
    public sealed record SigningKeyHistoryItem(string Fingerprint, string PublicKeyPem, string RetiredUtc);
    public sealed record SigningKeyStatusItem(string CurrentFingerprint, string CurrentPublicKeyPem, IReadOnlyList<string> TrustedFingerprints, IReadOnlyList<SigningKeyHistoryItem> History);
    public sealed record SigningKeyRotateRequest(string Actor);
    public sealed record SigningKeyRotateResult(string PreviousFingerprint, string NewFingerprint, string RotatedUtc);
    public sealed record AuthProviderRequest(string Name, string Kind, string ConfigJson, bool Enabled);
    public sealed record AuthProviderItem(int Id, string Name, string Kind, bool Enabled, string ConfigJson, string CreatedAt);
    public sealed record AuthProviderToggle(bool Enabled);
    public sealed record LegalHoldRequest(int ChannelId, DateTime FromUtc, DateTime ToUtc, string Reason, string CreatedBy);
    public sealed record LegalHoldRevoke(string By, string Reason);
    public sealed record LegalHoldItem(long Id, int ChannelId, string FromUtc, string ToUtc, string Reason, string CreatedBy, string CreatedAtUtc, string? RevokedAtUtc, string? RevokedBy, string? RevokedReason, bool Active);
    public sealed record AlertRuleRequest(string Name, string? EventType, int? ChannelId, string? Keyword, string? Channels, string? MatchEventTypes, int FrameMinutes, int MinEventsInWindow);
    public sealed record AlertRuleItem(long Id, string Name, string? EventType, int? ChannelId, string? Keyword, string? Channels, bool Enabled, string? MatchEventTypes, int FrameMinutes, int MinEventsInWindow);
    public sealed record ShareRequest(string Kind, string ResourcePath, string? Label, DateTime? ExpiresAt, int MaxUses);
    public sealed record ShareItem(int Id, string Token, string Kind, string ResourcePath, string? Label, string? CreatedBy, string? ExpiresAt, int MaxUses, int UseCount, bool Revoked, bool Active);
    public sealed record RedactionRequest(string SourceType, long RefId, int ChannelId, DateTime OccurredAtUtc, int X, int Y, int Width, int Height, bool Filled);
    public sealed record RedactionItem(long Id, string SourceType, long RefId, int ChannelId, string OccurredAtUtc, int X, int Y, int Width, int Height, bool Filled, string CreatedAtUtc);
    public sealed record ReplicationItem(long Id, string SourcePath, string DestinationPath, int IntervalMinutes, bool Enabled, string? LastRunUtc, string? LastResult, string? LastError, int ConsecutiveFailures, bool Due);
    public sealed record AlarmBoardItem(long EventId, int ChannelId, string EventType, string StartUtc, string Status, string Priority, string? DueUtc, string? Owner, string? AssignedTo, bool Overdue);
    public sealed record AlarmBoardSummaryItem(int Pending, int Acknowledged, int Actioned, int FalseAlarm, int Overdue);
    public sealed record DailyReportResponse(
        IReadOnlyList<RecordingSummaryRow> Recording,
        IReadOnlyList<CapacityTrendRow> Capacity,
        long Disconnects,
        IReadOnlyList<AiEventCountRow> Events);
    public sealed record ScheduleBody(long Id, int ChannelId, int DaysMask, int StartMinute, int EndMinute, bool Enabled);
    public sealed record ScheduleUpsertRequest(int ChannelId, int DaysMask, int StartMinute, int EndMinute, bool Enabled);
    public sealed record PatrolStepBody(string PresetName, int DwellSeconds);
    public sealed record LicenseStatusResponse(
        string Decision,
        string? Message,
        int MaxCameras,
        bool AllowsNewRecording,
        string[] Features,
        LicenseFeatureStatus[] FeatureStatus,
        DateTime? ExpiresUtc,
        string? ExpiryMessage,
        DateTime EvaluatedUtc);

    public sealed record LicenseFeatureStatus(string Feature, string Name, bool Allowed);
    public sealed record PatrolUpsertRequest(string Name, int ChannelId, bool Enabled, string? WindowStart, string? WindowEnd, IReadOnlyList<PatrolStepBody>? Steps);

    private static readonly TimeSpan ReconWindow = TimeSpan.FromSeconds(10);

    /// <summary>門禁匯入去重窗：僅吸收控制器短時間內的重送，不吞掉正常重複刷卡。</summary>
    private static readonly TimeSpan DoorDedupeWindow = TimeSpan.FromSeconds(5);

    public static void MapAll(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", static (SqliteStore store, ChannelRepository channels) =>
            Results.Ok(new HealthResponse("ok", "ok", channels.List().Count)));

        // 節點自我身分（§14.1 #10，M295）：中央站以 GET /api/node 探測子節點。
        // 需金鑰（不在 /api/health 那條永遠開放的白名單內），因為它會揭露版本與路數。
        api.MapGet("/node", static (ChannelRepository channels) =>
            Results.Ok(new NodeStatus(
                HeliVMS.Licensing.MachineIdProvider.GetDeviceCode(),
                Environment.MachineName,
                ApiVersion(),
                channels.List().Count)));

        // 中央站聚合視圖（M295）：探測所有啟用中的子節點後回傳狀態快照（不含各子節點金鑰）。
        api.MapGet("/central/nodes", static async (CentralNodeService central, CancellationToken cancellation) =>
        {
            var nodes = await central.ProbeAllAsync(cancellation).ConfigureAwait(false);
            return Results.Ok(nodes.Select(ToCentralNodeStatus).ToArray());
        });

        // 中央節點管理（M299／§14.1 #10）：新增、更新、刪除與單節點探測。
        // 新增／更新後立即探測一次，讓 UI 不必再按一次就有狀態；金鑰永不回傳。
        api.MapPost("/central/nodes", static async (
            CentralNodeUpsertRequest body,
            CentralNodeRepository repo,
            CentralNodeService central,
            CancellationToken cancellation) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.BaseUrl))
            {
                return Results.BadRequest(new { error = "名稱與位址必填" });
            }

            var id = repo.Add(body.Name.Trim(), body.BaseUrl.Trim(), body.ApiKey ?? string.Empty, body.Enabled ?? true);
            var node = central.List().First(n => n.Id == id);
            var probed = await central.ProbeAsync(node, cancellation).ConfigureAwait(false);
            return Results.Created($"/api/central/nodes/{id}", ToCentralNodeStatus(probed));
        });

        // 中央站聚合各子節點的頻道清單（§14.1 #10，M302）：讓上層看得到跨機頻道，
        // 回放面板也能列出「某節點的第幾路」而不必使用者自己猜編號。與 /central/nodes 同為
        // 管理視圖，沿用 M295 決策不加旗標閘門；子節點金鑰不進回應。
        api.MapGet("/central/channels", static async (CentralNodeService service, CancellationToken cancellation) =>
            Results.Ok(await service.ListChannelsAsync(cancellation).ConfigureAwait(false)));

        // 中央站彙整各子節點最近的警報看板（§14.1 #10，M303）：跨機值班要能一眼看到「哪台機器
        // 現在有未處理／逾期的事件」。與 /central/channels 同為管理視圖，沿用 M295 決策不加
        // 旗標閘門；只帶節點識別與名稱，子節點金鑰不進回應。
        api.MapGet("/central/alarms", static async (int? take, CentralNodeService service, CancellationToken cancellation) =>
            Results.Ok(await service.ListAlarmsAsync(take ?? 100, cancellation).ConfigureAwait(false)));

        api.MapPut("/central/nodes/{id:long}", static async (
            long id,
            CentralNodeUpdateRequest body,
            CentralNodeRepository repo,
            CentralNodeService central,
            CancellationToken cancellation) =>
        {
            if (repo.List().All(n => n.Id != id))
            {
                return Results.NotFound();
            }

            if ((body.Name is not null && string.IsNullOrWhiteSpace(body.Name))
                || (body.BaseUrl is not null && string.IsNullOrWhiteSpace(body.BaseUrl)))
            {
                return Results.BadRequest(new { error = "名稱與位址不可為空" });
            }

            repo.Update(id, body.Name?.Trim(), body.BaseUrl?.Trim(), body.ApiKey, body.Enabled);
            var node = central.List().First(n => n.Id == id);
            var probed = await central.ProbeAsync(node, cancellation).ConfigureAwait(false);
            return Results.Ok(ToCentralNodeStatus(probed));
        });

        api.MapDelete("/central/nodes/{id:long}", static (long id, CentralNodeRepository repo) =>
        {
            if (repo.List().All(n => n.Id != id))
            {
                return Results.NotFound();
            }

            repo.Delete(id);
            return Results.NoContent();
        });

        api.MapPost("/central/nodes/{id:long}/probe", static async (
            long id,
            CentralNodeRepository repo,
            CentralNodeService central,
            CancellationToken cancellation) =>
        {
            var node = repo.List().FirstOrDefault(n => n.Id == id);
            if (node is null)
            {
                return Results.NotFound();
            }

            var probed = await central.ProbeAsync(node, cancellation).ConfigureAwait(false);
            return Results.Ok(ToCentralNodeStatus(probed));
        });

        // 跨機回放代理（§14.1 #10，M301）：上層 NVR 轉發子節點的 HLS VOD。
        // 瀏覽器只跟上層說話，子節點金鑰留在伺服器；清單中的片段網址回傳前改寫為代理路徑。
        // 屬 remote 能力（與 /api/stream 同理由）：這是「遠端存取」而非本機播放。
        api.MapGet("/central/playback/{nodeId:long}/playlist.m3u8", HandleCentralPlaylist);
        api.MapGet("/central/playback/{nodeId:long}/segment/{segmentId:long}/init.mp4", HandleCentralSegmentInit);
        api.MapGet("/central/playback/{nodeId:long}/segment/{segmentId:long}/media.m4s", HandleCentralSegmentMedia);

        api.MapGet("/channels", static (ChannelRepository r) => Results.Ok(r.List()));

        // 授權狀態（M210）。刻意不放進 LicenseGateMiddleware 的規則表：前端要先問
        // 「我被擋在哪一個旗標」才畫得出畫面，狀態查詢本身不該被授權擋住。
        api.MapGet("/license", static (LicenseService license, TimeProvider clock) =>
        {
            var nowUtc = clock.GetUtcNow().UtcDateTime;
            var result = license.Evaluate(nowUtc);
            var expiry = result.Expiry(nowUtc);
            return Results.Ok(new LicenseStatusResponse(
                result.Decision.ToString(),
                result.Message,
                result.MaxCameras,
                result.AllowsNewRecording,
                [.. result.Features],
                [.. LicenseFeatures.All.Select(f => new LicenseFeatureStatus(
                    f,
                    LicenseFeatures.DisplayName(f),
                    result.AllowsFeature(f)))],
                expiry.ExpiresUtc,
                expiry.Message,
                nowUtc));
        });

        api.MapGet("/events", HandleEvents);

        api.MapGet("/events/search", HandleForensicSearch);

        api.MapPost("/events/search/semantic", HandleSemanticSearch);

        api.MapGet("/alarms/summary", static (AlarmTriageRepository r) =>
            Results.Ok(r.Summarize(DateTime.UtcNow)));

        api.MapGet("/alarms/board", (int? take, AlarmTriageRepository r) =>
            Results.Ok(r.ListBoard(DateTime.UtcNow, Math.Clamp(take ?? 100, 1, 500))));

        api.MapPost("/events/{id:long}/ack", static (long id, AckRequest body, AlarmEventRepository r, AlertBroadcastHub hub) =>
        {
            r.Acknowledge(id, body.Acknowledged);
            hub.Publish(new AlertUpdate("alarm.ack", id, body.Acknowledged ? "acknowledged" : "pending", null));
            return Results.Ok();
        });

        api.MapPost("/events/{id:long}/disposition", static (long id, DispositionRequest body, AlarmEventRepository r, AlertBroadcastHub hub) =>
        {
            r.SetDisposition(id, body.Status, body.AssignedTo, body.Note, DateTime.UtcNow);
            hub.Publish(new AlertUpdate("alarm.disposition", id, body.Status, null));
            return Results.Ok();
        });

        api.MapPost("/events/{id:long}/triage", static (long id, TriageRequest body, AlarmTriageRepository r, AlertBroadcastHub hub) =>
        {
            r.SetTriage(id, body.Priority, body.DueUtc, body.Owner, DateTime.UtcNow);
            hub.Publish(new AlertUpdate("alarm.triage", id, null, body.Priority));
            return Results.Ok();
        });

        api.MapGet("/pos", HandlePos);

        // POS 交易匯入端點：桌面端先前只能以 CSV 手動匯入；此端點讓收銀機／中介服務
        // 直接把交易推進 VMS，與桌面匯入共用同一去重視窗語意（PosReconciliation）。
        api.MapPost("/pos", HandlePosIngest);

        api.MapGet("/pos/recon", HandlePosRecon);

        api.MapGet("/pos/anomalies", HandlePosAnomalies);

        api.MapGet("/smartwall/board", HandleSmartwallBoard);

        api.MapGet("/recording/timeline", HandleRecordingTimeline);

        api.MapGet("/recording/segments", HandleRecordingSegments);

        api.MapGet("/stream/{channelId:int}/playlist.m3u8", HandleStreamPlaylist);

        api.MapGet("/stream/segment/{id:long}/init.mp4", HandleSegmentInit);

        api.MapGet("/stream/segment/{id:long}/media.m4s", HandleSegmentMedia);

        api.MapGet("/audit", HandleAudit);

        api.MapGet("/audit/export.csv", HandleAuditExport);

        api.MapGet("/reports/daily", HandleDailyReport);

        api.MapGet("/recording/schedules", static (RecordingScheduleRepository repo) =>
            Results.Ok(repo.List()));

        api.MapPost("/recording/schedules", static (ScheduleUpsertRequest body, RecordingScheduleRepository repo) =>
        {
            if (body.ChannelId <= 0)
            {
                return Results.BadRequest(new { error = "頻道須為正整數" });
            }

            if (body.DaysMask is < 0 or > 127)
            {
                return Results.BadRequest(new { error = "星期遮罩須為 0..127" });
            }

            if (body.StartMinute is < 0 or > 1439 || body.EndMinute is < 0 or > 1439)
            {
                return Results.BadRequest(new { error = "時段須在 0..1439 分" });
            }

            if (body.EndMinute < body.StartMinute)
            {
                return Results.BadRequest(new { error = "結束須不早於開始" });
            }

            var saved = repo.Upsert(new RecordingScheduleRecord
            {
                ChannelId = body.ChannelId,
                DaysMask = body.DaysMask,
                StartMinute = body.StartMinute,
                EndMinute = body.EndMinute,
                Enabled = body.Enabled,
            });
            return Results.Ok(new ScheduleBody(saved.Id, saved.ChannelId, saved.DaysMask, saved.StartMinute, saved.EndMinute, saved.Enabled));
        });

        api.MapDelete("/recording/schedules/{id:long}", static (long id, RecordingScheduleRepository repo) =>
        {
            if (!repo.List().Any(s => s.Id == id))
            {
                return Results.NotFound();
            }

            repo.Delete(id);
            return Results.Ok();
        });

        api.Map("/alerts/ws", HandleAlertStream);

        api.MapGet("/patrols", static (PatrolRepository repo) => Results.Ok(repo.ListAll()));

        api.MapPost("/patrols", static (PatrolUpsertRequest body, PatrolRepository repo) =>
        {
            try
            {
                var id = repo.Save(
                    null,
                    body.Name,
                    body.ChannelId,
                    body.Enabled,
                    body.WindowStart ?? "00:00",
                    body.WindowEnd ?? "23:59",
                    DateTime.UtcNow,
                    (body.Steps ?? []).Select(s => new PatrolStepRow(s.PresetName, s.DwellSeconds)).ToList());
                return Results.Created($"/api/patrols/{id}", new { id });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapPut("/patrols/{id:long}", static (long id, PatrolUpsertRequest body, PatrolRepository repo) =>
        {
            if (!repo.ListAll().Any(p => p.Id == id))
            {
                return Results.NotFound();
            }

            try
            {
                repo.Save(
                    id,
                    body.Name,
                    body.ChannelId,
                    body.Enabled,
                    body.WindowStart ?? "00:00",
                    body.WindowEnd ?? "23:59",
                    DateTime.UtcNow,
                    (body.Steps ?? []).Select(s => new PatrolStepRow(s.PresetName, s.DwellSeconds)).ToList());
                return Results.Ok(new { id });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapDelete("/patrols/{id:long}", static (long id, PatrolRepository repo) =>
            repo.Delete(id) ? Results.Ok() : Results.NotFound());

        api.MapPost("/accounts/authenticate", static (LoginRequest body, AuthService auth, AccountSessionStore sessions) =>
        {
            if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
            {
                return Results.Json(new { error = "請輸入帳號與密碼" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = auth.Authenticate(body.Username, body.Password);
            if (!result.Succeeded)
            {
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // M306：登入成功即簽發工作階段 token。SPA 之後以 Bearer token 呼叫 /api/*，
            // 不需要另外餵一顆 API 金鑰（金鑰仍可用，兩者同層）。
            var username = body.Username.Trim();
            var token = sessions.Create(username, result.Role, result.DisplayName);
            return Results.Ok(new { role = result.Role, displayName = result.DisplayName, token });
        });

        api.MapPost("/accounts/logout", static (HttpContext context, AccountSessionStore sessions) =>
        {
            // 登出是無條件成功：撤銷目前憑證（若有），但不會因為「已經失效」而讓使用者卡住。
            var header = context.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                sessions.Revoke(header[7..].Trim());
            }

            return Results.Ok();
        });

        api.MapGet("/accounts", static (UserRepository users) =>
            Results.Ok(Accounts(users)));

        api.MapPost("/accounts", static (AccountUpsertRequest body, UserRepository users) =>
        {
            if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
            {
                return Results.BadRequest(new { error = "帳號與密碼必填" });
            }

            int id;
            try
            {
                id = users.CreateUser(body.Username.Trim(), PasswordHasher.Hash(body.Password), body.Role, body.DisplayName);
            }
            catch (SqliteException)
            {
                return Results.Conflict(new { error = "帳號重複" });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            return Results.Created($"/api/accounts/{id}", new { id });
        });

        api.MapPatch("/accounts/{id:int}", static (int id, AccountPatchRequest body, UserRepository users) =>
        {
            var existing = users.GetById(id);
            if (existing is null)
            {
                return Results.NotFound();
            }

            if (body.Role is not null)
            {
                try
                {
                    users.SetRole(id, body.Role);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }

            if (body.Enabled is not null)
            {
                users.SetEnabled(id, body.Enabled.Value);
            }

            if (body.DisplayName is not null)
            {
                users.SetDisplayName(id, string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName);
            }

            return Results.Ok(new { id });
        });

        api.MapDelete("/accounts/{id:int}", static (int id, UserRepository users) =>
        {
            if (users.GetById(id) is null)
            {
                return Results.NotFound();
            }

            users.DeleteUser(id);
            return Results.Ok();
        });

        api.MapGet("/config", static (SettingsRepository settings) =>
            Results.Ok(new ConfigResponse(
                settings.GetOrDefault("auth.enabled", "0") == "1",
                (int)settings.GetDoubleOrDefault("auth.lockout.threshold", 5),
                (int)settings.GetDoubleOrDefault("auth.lockout.minutes", 5),
                (int)settings.GetDoubleOrDefault(RetentionService.DaysKey, 30),
                settings.GetDoubleOrDefault(RetentionService.WatermarkGbKey, 0),
                (int)settings.GetDoubleOrDefault(RetentionService.AlarmDaysKey, 365),
                (int)settings.GetDoubleOrDefault(RetentionService.AuditDaysKey, 365))));

        api.MapPut("/config", static (ConfigRequest body, SettingsRepository settings) =>
        {
            if (body.AuthEnabled is not null)
            {
                settings.Set("auth.enabled", body.AuthEnabled.Value ? "1" : "0");
            }

            if (body.LockoutThreshold is int threshold)
            {
                if (threshold is < 1 or > 99)
                {
                    return Results.BadRequest(new { error = "鎖定次數須為 1..99" });
                }

                settings.Set("auth.lockout.threshold", threshold.ToString());
            }

            if (body.LockoutMinutes is int minutes)
            {
                if (minutes is < 1 or > 1440)
                {
                    return Results.BadRequest(new { error = "鎖定分鐘須為 1..1440" });
                }

                settings.Set("auth.lockout.minutes", minutes.ToString());
            }

            if (body.RecordingRetentionDays is int days)
            {
                if (days is < 1 or > 3650)
                {
                    return Results.BadRequest(new { error = "保留天數須為 1..3650" });
                }

                settings.Set(RetentionService.DaysKey, days.ToString());
            }

            if (body.RecordingWatermarkGb is double watermark)
            {
                if (watermark is < 0 or > 99999)
                {
                    return Results.BadRequest(new { error = "浮水印須為 0..99999GB（0＝關閉）" });
                }

                settings.Set(RetentionService.WatermarkGbKey, watermark.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (body.AlarmRetentionDays is int alarmDays)
            {
                if (alarmDays is < 1 or > 36500)
                {
                    return Results.BadRequest(new { error = "警報保留天數須為 1..36500" });
                }

                settings.Set(RetentionService.AlarmDaysKey, alarmDays.ToString());
            }

            if (body.AuditRetentionDays is int auditDays)
            {
                if (auditDays is < 0 or > 36500)
                {
                    return Results.BadRequest(new { error = "稽核保留天數須為 0..36500（0＝永久保留）" });
                }

                settings.Set(RetentionService.AuditDaysKey, auditDays.ToString());
            }

            return Results.Ok(new ConfigResponse(
                settings.GetOrDefault("auth.enabled", "0") == "1",
                (int)settings.GetDoubleOrDefault("auth.lockout.threshold", 5),
                (int)settings.GetDoubleOrDefault("auth.lockout.minutes", 5),
                (int)settings.GetDoubleOrDefault(RetentionService.DaysKey, 30),
                settings.GetDoubleOrDefault(RetentionService.WatermarkGbKey, 0),
                (int)settings.GetDoubleOrDefault(RetentionService.AlarmDaysKey, 365),
                (int)settings.GetDoubleOrDefault(RetentionService.AuditDaysKey, 365)));
        });

        api.MapGet("/config/usage", static (RetentionService retention) =>
            Results.Ok(new UsageResponse(
                retention.UsageBytes,
                retention.UsageBytes / 1073741824.0,
                retention.RetentionDays,
                retention.WatermarkGb,
                retention.AlarmRetentionDays,
                retention.AuditRetentionDays)));

        api.MapPost("/retention/run", static (RetentionService retention) =>
        {
            var result = retention.RunOnce(DateTime.UtcNow);
            return Results.Ok(new RetentionRunResult(
                result.AgePurged, result.WatermarkPurged, result.BytesFreed, result.AlarmPurged, result.Skipped,
                result.AuditPurged, result.AudioPurged));
        });

        api.MapGet("/evidence", static (EvidenceManifestRepository manifests) =>
        {
            var items = manifests.List()
                .Select(m =>
                {
                    var count = 0;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(m.ManifestJson);
                        if (doc.RootElement.TryGetProperty("Items", out var arr))
                        {
                            count = arr.GetArrayLength();
                        }
                    }
                    catch (System.Text.Json.JsonException)
                    {
                    }

                    return new EvidenceListItem(m.Id, m.Status, m.CreatedAt, m.LastVerifiedAt, count);
                })
                .ToList();
            return Results.Ok(items);
        });

        api.MapPost("/evidence/package", static (EvidencePackageRequest body, EvidenceManifestRepository manifests, PathAccessPolicy paths) =>
        {
            string bundleName;
            try
            {
                bundleName = PathAccessPolicy.ValidateBundleName(body.BundleName);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (body.Files is null or { Count: 0 })
            {
                return Results.BadRequest(new { error = "至少一個來源檔案" });
            }

            foreach (var file in body.Files)
            {
                if (string.IsNullOrWhiteSpace(file))
                {
                    return Results.BadRequest(new { error = "來源檔不可為空" });
                }

                if (!File.Exists(file))
                {
                    return Results.BadRequest(new { error = $"來源檔不存在：{file}" });
                }

                if (!paths.IsAllowed(file))
                {
                    return Results.Json(new { error = paths.DenyMessage(file) }, statusCode: StatusCodes.Status403Forbidden);
                }
            }

            EvidencePackager.BundleManifest manifest;
            try
            {
                manifest = EvidencePackager.BuildManifest(bundleName, body.Files);
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var outDir = paths.EvidenceDir;
            Directory.CreateDirectory(outDir);
            var bundlePath = Path.Combine(outDir, $"{bundleName}-{manifest.BundleId:N}.evp");
            string bundleSha;
            try
            {
                bundleSha = EvidencePackager.Create(bundlePath, manifest, body.Password);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            manifests.Upsert(
                outDir,
                System.Text.Json.JsonSerializer.Serialize(manifest),
                "packaged");
            return Results.Ok(new EvidencePackageResult(
                bundlePath,
                bundleSha,
                manifest.Items.Count,
                manifest.CreatedUtc.ToString("O")));
        });

        api.MapPost("/evidence/verify", static (EvidenceVerifyRequest body, PathAccessPolicy paths) =>
        {
            if (string.IsNullOrWhiteSpace(body.BundlePath) || !File.Exists(body.BundlePath))
            {
                return Results.BadRequest(new { error = "包檔不存在" });
            }

            if (!paths.IsEvidencePath(body.BundlePath))
            {
                return Results.Json(new { error = paths.DenyMessage(body.BundlePath) }, statusCode: StatusCodes.Status403Forbidden);
            }

            EvidencePackager.VerifyResult result;
            try
            {
                result = EvidencePackager.Verify(body.BundlePath, body.Password);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            return Results.Ok(new EvidenceVerifyResult(
                result.Valid,
                result.Expired,
                result.Failures,
                result.Items
                    .Select(i => new EvidenceItemBody(i.RelativePath, i.Sha256, i.SizeBytes, i.Kind))
                    .ToList()));
        });

        api.MapGet("/backup/runs", static (BackupRepository backups) => Results.Ok(backups.ListRuns()));

        api.MapPost("/backup/run", static (BackupRunRequest body, BackupService backup, PathAccessPolicy paths) =>
        {
            if (string.IsNullOrWhiteSpace(body.SourceRoot) || string.IsNullOrWhiteSpace(body.TargetRoot))
            {
                return Results.BadRequest(new { error = "來源與目標目錄必填" });
            }

            if (!Directory.Exists(body.SourceRoot))
            {
                return Results.BadRequest(new { error = "來源目錄不存在" });
            }

            if (!paths.IsAllowed(body.SourceRoot))
            {
                return Results.Json(new { error = paths.DenyMessage(body.SourceRoot) }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (!paths.IsAllowed(body.TargetRoot))
            {
                return Results.Json(new { error = paths.DenyMessage(body.TargetRoot) }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var result = backup.Run(body.SourceRoot, body.TargetRoot);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapGet("/door/events", static (int? deviceId, int? doorId, string? card, bool? granted, DateTime? from, DateTime? to, int? limit, DoorEventRepository doors) =>
        {
            var q = new DoorEventQuery(
                deviceId,
                doorId,
                card,
                granted,
                from ?? DateTime.UtcNow.AddDays(-1),
                to,
                Math.Clamp(limit ?? 100, 1, 500));

            return Results.Ok(doors.Query(q)
                .Select(d => new DoorEventItem(d.Id, d.DeviceId, d.DoorId, d.CardId, d.Direction, d.Granted, d.Reason, SqliteStore.Iso(d.OccurredAtUtc)))
                .ToList());
        });

        // 門禁事件匯入端點：門禁控制器／中介服務可直接推送刷卡事件；以去重窗吸收重送。
        api.MapPost("/door/events", HandleDoorIngest);

        api.MapGet("/detections", static (int? channelId, string? @class, float? minConfidence, DateTime? from, DateTime? to, int? limit, DetectionRepository detections) =>
        {
            var q = new DetectionRepository.QueryArgs
            {
                ChannelId = channelId,
                Class = string.IsNullOrWhiteSpace(@class) ? null : @class,
                MinConfidence = minConfidence,
                FromUtc = from ?? DateTime.UtcNow.AddDays(-1),
                ToUtc = to ?? DateTime.UtcNow,
                Limit = Math.Clamp(limit ?? 200, 1, 500),
            };

            return Results.Ok(detections.ListByQuery(q)
                .Select(d => new DetectionItem(d.Id, d.ChannelId, d.Class, d.Confidence, d.X, d.Y, d.W, d.H, SqliteStore.Iso(d.DetectedUtc)))
                .ToList());
        });

        api.MapGet("/detections/summary", static (int? channelId, string? @class, float? minConfidence, DateTime? from, DateTime? to, DetectionRepository detections) =>
        {
            var q = new DetectionRepository.QueryArgs
            {
                ChannelId = channelId,
                Class = string.IsNullOrWhiteSpace(@class) ? null : @class,
                MinConfidence = minConfidence,
                FromUtc = from ?? DateTime.UtcNow.AddDays(-1),
                ToUtc = to ?? DateTime.UtcNow,
            };

            return Results.Ok(detections.CountByClass(q)
                .Select(c => new DetectionSummaryItem(c.Class, c.Count))
                .ToList());
        });

        api.MapGet("/notifications", static (int? limit, NotificationLogRepository logs) =>
        {
            var q = Math.Clamp(limit ?? 50, 1, 500);
            return Results.Ok(logs.ListRecent(q)
                .Select(l => new NotificationLogItem(l.Id, SqliteStore.Iso(l.TsUtc), l.ChannelId, l.EventType, l.Route, l.Ok, l.Attempts, l.Detail))
                .ToList());
        });

        api.MapGet("/exports", static (ExportJobRepository jobs) => Results.Ok(jobs.List()
            .Select(j => new ExportJobItem(j.Id, j.ChannelId, j.Stream, SqliteStore.Iso(j.StartUtc), SqliteStore.Iso(j.EndUtc), j.Status, j.OutputPath, j.FileSizeBytes, j.Sha256, j.Error, SqliteStore.Iso(j.CreatedUtc)))
            .ToList()));

        api.MapPost("/exports", static (ExportJobRequest body, ExportJobRepository jobs) =>
        {
            if (body.ChannelId < 1)
            {
                return Results.BadRequest(new { error = "頻道必填" });
            }

            if (string.IsNullOrWhiteSpace(body.Stream) || body.ToUtc <= body.FromUtc)
            {
                return Results.BadRequest(new { error = "串流與時間窗必須有效" });
            }

            var id = jobs.Enqueue(body.ChannelId, body.Stream, body.FromUtc, body.ToUtc);
            var job = jobs.Get(id);
            return Results.Ok(new ExportJobItem(job!.Id, job.ChannelId, job.Stream, SqliteStore.Iso(job.StartUtc), SqliteStore.Iso(job.EndUtc), job.Status, job.OutputPath, job.FileSizeBytes, job.Sha256, job.Error, SqliteStore.Iso(job.CreatedUtc)));
        });

        // M240/M241：遠端取走匯出檔後要能自己確認出處——重算雜湊、驗簽、比對簽署者指紋。
        api.MapGet("/exports/{id:long}/verify", static (long id, string? signer, ExportJobRepository jobs, ExportReceiptService receipts) =>
        {
            var job = jobs.Get(id);
            if (job is null)
            {
                return Results.NotFound(new { error = "找不到匯出工作" });
            }

            if (string.IsNullOrWhiteSpace(job.OutputPath))
            {
                return Results.BadRequest(new { error = "此工作尚未產生匯出檔" });
            }

            // 未指定 signer 時信任本機全部指紋（現行＋M241 換發保留的歷史金鑰），
            // 否則換發後正當的舊收據會被誤判為無效。
            var trusted = string.IsNullOrWhiteSpace(signer)
                ? receipts.TrustedSigners()
                : new TrustedSignerSet(new[] { signer });

            // 只輸出驗證結果，不回傳本機絕對路徑（與 M239 遮蔽 file_path 同一個理由）。
            var report = ExportReceiptCodec.Verify(job.OutputPath, trusted);
            return Results.Ok(new ExportVerifyItem(
                job.Id,
                report.ReceiptExists,
                report.HashMatches,
                report.SignatureValid,
                report.SignerMatched,
                report.SelfAssertedKey,
                report.Signer,
                report.Valid,
                report.Detail));
        });

        // M241：簽章金鑰現況。公鑰可以公開（就是要交給驗證方），私鑰永不離開本機。
        api.MapGet("/evidence/signing-keys", static (ExportReceiptService receipts) =>
        {
            var history = receipts.SigningKeyHistory();
            return Results.Ok(new SigningKeyStatusItem(
                CurrentFingerprint: receipts.SignerFingerprint(),
                CurrentPublicKeyPem: receipts.SignerPublicKeyPem(),
                TrustedFingerprints: receipts.TrustedSigners().Fingerprints,
                History: history
                    .Select(k => new SigningKeyHistoryItem(k.Fingerprint, k.PublicKeyPem, SqliteStore.Iso(k.RetiredUtc)))
                    .ToList()));
        });

        // M241：換發簽章金鑰。新收據改用新金鑰，舊公鑰保留以維持歷史收據可驗證。
        api.MapPost("/evidence/signing-keys/rotate", static (SigningKeyRotateRequest body, ExportReceiptService receipts) =>
        {
            if (string.IsNullOrWhiteSpace(body.Actor))
            {
                return Results.BadRequest(new { error = "操作者必填（換發簽章金鑰必須可歸責）" });
            }

            var rotation = receipts.RotateSigningKey(body.Actor.Trim());
            return Results.Ok(new SigningKeyRotateResult(
                rotation.PreviousFingerprint,
                rotation.NewFingerprint,
                SqliteStore.Iso(rotation.RotatedUtc)));
        });

        api.MapGet("/auth/providers", static (AuthProviderRepository providers) =>
            Results.Ok(providers.List()
                .Select(p => new AuthProviderItem(p.Id, p.Name, p.Kind, p.Enabled, p.ConfigJson, p.CreatedAt))
                .ToList()));

        api.MapPost("/auth/providers", static (AuthProviderRequest body, AuthProviderRepository providers) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name) ||
                (body.Kind != AuthProviderRepository.KindLdap && body.Kind != AuthProviderRepository.KindOidc))
            {
                return Results.BadRequest(new { error = "名稱與類型（ldap/oidc）必填" });
            }

            if (string.IsNullOrWhiteSpace(body.ConfigJson))
            {
                return Results.BadRequest(new { error = "設定 JSON 必填" });
            }

            try
            {
                System.Text.Json.JsonDocument.Parse(body.ConfigJson);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(new { error = "設定必須是合法 JSON" });
            }

            try
            {
                var id = providers.Add(body.Name, body.Kind, body.ConfigJson, body.Enabled);
                return Results.Ok(new AuthProviderItem(id, body.Name, body.Kind, body.Enabled, body.ConfigJson, ""));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"新增失敗：{ex.Message}" });
            }
        });

        api.MapPut("/auth/providers/{id:int}", static (int id, AuthProviderToggle body, AuthProviderRepository providers) =>
        {
            if (providers.Get(id) is null)
            {
                return Results.NotFound();
            }

            providers.SetEnabled(id, body.Enabled);
            return Results.Ok(new { ok = true });
        });

        api.MapDelete("/auth/providers/{id:int}", static (int id, AuthProviderRepository providers) =>
        {
            if (providers.Get(id) is null)
            {
                return Results.NotFound();
            }

            providers.Delete(id);
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/legal-holds", static (LegalHoldRepository holds) =>
            Results.Ok(ToHolds(holds.ListAll())));

        api.MapGet("/legal-holds/active", static (LegalHoldRepository holds) =>
            Results.Ok(ToHolds(holds.ListActive())));

        api.MapPost("/legal-holds", static (LegalHoldRequest body, LegalHoldRepository holds) =>
        {
            if (body.ChannelId < 1 || body.ToUtc <= body.FromUtc || string.IsNullOrWhiteSpace(body.Reason))
            {
                return Results.BadRequest(new { error = "頻道與時間窗與原因必填且需有效" });
            }

            var by = string.IsNullOrWhiteSpace(body.CreatedBy) ? "operator" : body.CreatedBy;
            var id = holds.Add(body.ChannelId, body.FromUtc, body.ToUtc, body.Reason, by, DateTime.UtcNow);
            var record = holds.ListAll().First(h => h.Id == id);
            return Results.Ok(ToHolds([record])[0]);
        });

        api.MapPut("/legal-holds/{id:long}/revoke", static (long id, LegalHoldRevoke body, LegalHoldRepository holds) =>
        {
            var record = holds.ListAll().FirstOrDefault(h => h.Id == id);
            if (record is null)
            {
                return Results.NotFound();
            }

            var ok = holds.Revoke(id, string.IsNullOrWhiteSpace(body.By) ? "operator" : body.By, body.Reason ?? "", DateTime.UtcNow);
            return ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = "撤銷失敗" });
        });

        api.MapGet("/alert-rules", static (AlertRuleRepository rules) =>
            Results.Ok(rules.ListAll().Select(ToRule).ToList()));

        api.MapPost("/alert-rules", static (AlertRuleRequest body, AlertRuleRepository rules) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.BadRequest(new { error = "規則名稱必填" });
            }

            var id = rules.Add(body.Name, NullIfBlank(body.EventType), body.ChannelId, NullIfBlank(body.Keyword), NullIfBlank(body.Channels), NullIfBlank(body.MatchEventTypes), body.FrameMinutes, body.MinEventsInWindow < 1 ? 1 : body.MinEventsInWindow);
            return Results.Ok(new AlertRuleItem(id, body.Name, body.EventType, body.ChannelId, body.Keyword, body.Channels, true, body.MatchEventTypes, body.FrameMinutes, body.MinEventsInWindow));
        });

        api.MapPut("/alert-rules/{id:long}", static (long id, AlertRuleRequest body, AlertRuleRepository rules) =>
        {
            var existing = rules.ListAll().FirstOrDefault(r => r.Id == id);
            if (existing is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.BadRequest(new { error = "規則名稱必填" });
            }

            rules.Update(id, body.Name, NullIfBlank(body.EventType), body.ChannelId, NullIfBlank(body.Keyword), NullIfBlank(body.Channels), existing.Enabled, NullIfBlank(body.MatchEventTypes), body.FrameMinutes, body.MinEventsInWindow < 1 ? 1 : body.MinEventsInWindow);
            return Results.Ok(new AlertRuleItem(id, body.Name, body.EventType, body.ChannelId, body.Keyword, body.Channels, existing.Enabled, body.MatchEventTypes, body.FrameMinutes, body.MinEventsInWindow));
        });

        api.MapPut("/alert-rules/{id:long}/enabled", static (long id, AuthProviderToggle body, AlertRuleRepository rules) =>
        {
            if (rules.ListAll().All(r => r.Id != id))
            {
                return Results.NotFound();
            }

            rules.SetEnabled(id, body.Enabled);
            return Results.Ok(new { ok = true });
        });

        api.MapDelete("/alert-rules/{id:long}", static (long id, AlertRuleRepository rules) =>
        {
            if (rules.ListAll().All(r => r.Id != id))
            {
                return Results.NotFound();
            }

            rules.Delete(id);
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/shares", static (ShareLinkRepository shares) => Results.Ok(ToShares(shares.List())));

        api.MapPost("/shares", static (ShareRequest body, ShareLinkRepository shares, ShareLinkService links, PathAccessPolicy paths) =>
        {
            if (!ShareKind.IsValid(body.Kind) || string.IsNullOrWhiteSpace(body.ResourcePath))
            {
                return Results.BadRequest(new { error = "類型（segment/snapshot/evidence）與資源路徑必填" });
            }

            if (!paths.IsAllowed(body.ResourcePath))
            {
                return Results.Json(new { error = paths.DenyMessage(body.ResourcePath) }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var record = links.Create(
                    body.Kind,
                    body.ResourcePath,
                    paths.Roots,
                    DateTime.UtcNow,
                    NullIfBlank(body.Label),
                    null,
                    body.ExpiresAt,
                    body.MaxUses < 0 ? 0 : body.MaxUses);
                return Results.Ok(ToShare(record));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (FileNotFoundException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapPut("/shares/{id:int}/revoke", static (int id, ShareLinkRepository shares, ShareLinkService links) =>
        {
            if (shares.Get(id) is null)
            {
                return Results.NotFound();
            }

            links.Revoke(id);
            return Results.Ok(new { ok = true });
        });

        api.MapDelete("/shares/{id:int}", static (int id, ShareLinkRepository shares, ShareLinkService links) =>
        {
            if (shares.Get(id) is null)
            {
                return Results.NotFound();
            }

            links.Delete(id);
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/redactions", static (string? sourceType, long? refId, int? channelId, DateTime? from, DateTime? to, int? limit, RedactionRepository redactions) =>
        {
            var cap = Math.Clamp(limit ?? 200, 1, 500);
            IReadOnlyList<RedactionRegion> rows;
            if (!string.IsNullOrWhiteSpace(sourceType) && refId is not null)
            {
                rows = redactions.QueryBySource(sourceType, refId.Value);
            }
            else if (channelId is int ch)
            {
                rows = redactions.QueryByTime(ch, from ?? DateTime.MinValue, to ?? DateTime.MaxValue);
            }
            else
            {
                rows = redactions.QueryByTimeGlobal(from ?? DateTime.MinValue, to ?? DateTime.MaxValue);
            }

            return Results.Ok(rows.Take(cap).Select(ToRedaction).ToList());
        });

        api.MapPost("/redactions", static (RedactionRequest body, RedactionRepository redactions) =>
        {
            if (body.SourceType != RedactionSources.Clip && body.SourceType != RedactionSources.Snapshot)
            {
                return Results.BadRequest(new { error = "類型須為 clip/snapshot" });
            }

            if (body.Width <= 0 || body.Height <= 0 || body.X < 0 || body.Y < 0)
            {
                return Results.BadRequest(new { error = "遮蔽區域須為正面積" });
            }

            var id = redactions.Add(body.SourceType, body.RefId, body.ChannelId, body.OccurredAtUtc, body.X, body.Y, body.Width, body.Height, body.Filled, DateTime.UtcNow);
            return Results.Ok(new RedactionItem(id, body.SourceType, body.RefId, body.ChannelId, SqliteStore.Iso(body.OccurredAtUtc), body.X, body.Y, body.Width, body.Height, body.Filled, SqliteStore.Iso(DateTime.UtcNow)));
        });

        api.MapDelete("/redactions/{id:long}", static (long id, RedactionRepository redactions) =>
            redactions.Remove(id) ? Results.Ok(new { ok = true }) : Results.NotFound());

        api.MapGet("/replication", static (OffsiteReplicationRepository repo) =>
            Results.Ok(repo.List().Select(j => new ReplicationItem(
                j.Id,
                j.SourcePath,
                j.DestinationPath,
                j.IntervalMinutes,
                j.Enabled,
                j.LastRunUtc is null ? null : SqliteStore.Iso(j.LastRunUtc.Value),
                j.LastResult,
                j.LastError,
                j.ConsecutiveFailures,
                OffsiteReplicationService.IsDue(j, DateTime.UtcNow))).ToList()));

        api.MapGet("/alarm-board/summary", static (AlarmTriageRepository triage) =>
        {
            var s = triage.Summarize(DateTime.UtcNow);
            return Results.Ok(new AlarmBoardSummaryItem(s.Pending, s.Acknowledged, s.Actioned, s.FalseAlarm, s.Overdue));
        });

        api.MapGet("/alarm-board", static (int? take, AlarmTriageRepository triage) =>
            Results.Ok(triage.ListBoard(DateTime.UtcNow, Math.Clamp(take ?? 100, 1, 500)).Select(ToBoard).ToList()));

        api.MapPut("/alarm-board/{eventId:long}/triage", static (long eventId, TriageRequest body, AlarmTriageRepository triage, AlertBroadcastHub hub) =>
        {
            if (!AlarmPriority.IsValid(body.Priority))
            {
                return Results.BadRequest(new { error = "優先序須為 low/normal/high/critical" });
            }

            triage.SetTriage(eventId, body.Priority, body.DueUtc, NullIfBlank(body.Owner), DateTime.UtcNow);
            hub.Publish(new AlertUpdate("alarm.triage", eventId, null, body.Priority));
            return Results.Ok(new { ok = true });
        });

        api.MapPut("/alarm-board/{eventId:long}/disposition", static (long eventId, DispositionRequest body, AlarmEventRepository events, AlertBroadcastHub hub) =>
        {
            if (!AlarmEventStatus.IsValid(body.Status))
            {
                return Results.BadRequest(new { error = "處置狀態須為 pending/acknowledged/actioned/false_alarm" });
            }

            events.SetDisposition(eventId, body.Status, NullIfBlank(body.AssignedTo), NullIfBlank(body.Note), DateTime.UtcNow);
            hub.Publish(new AlertUpdate("alarm.disposition", eventId, body.Status, null));
            return Results.Ok(new { ok = true });
        });

        api.MapPut("/alarm-board/{eventId:long}/ack", static (long eventId, InputAck body, AlarmEventRepository events, AlertBroadcastHub hub) =>
        {
            events.Acknowledge(eventId, body.Acknowledged);
            hub.Publish(new AlertUpdate("alarm.ack", eventId, body.Acknowledged ? "acknowledged" : "pending", null));
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/system-metrics", static () => Results.Ok(SystemMetricsService.Capture()));
        api.MapGet("/system-metrics/history", static () => Results.Ok(SystemMetricsService.CaptureHistory()));
        api.MapGet("/system-metrics/disks/history", static () => Results.Ok(SystemMetricsService.CaptureDiskHistory()));

        api.MapPost("/clip/search", static (ClipSearchRequest body, EmbeddingRepository repo, IEmbeddingProvider embedder) =>
        {
            var vector = body is null ? null : ResolveVector(body.Vector, body.Text, embedder);
            if (vector is null || vector.Length == 0)
            {
                return Results.BadRequest();
            }

            // M242：索引與查詢維度不同時，明確回 409 讓呼叫端去重建索引。
            // 靜默回傳空結果會讓人誤以為「沒有相似影片」，實際上是模型換版後索引過期。
            var stored = repo.StoredDimension();
            if (stored is not null && stored != vector.Length)
            {
                return Results.Json(
                    new { error = "查詢向量維度與既有索引不符，需重建索引。", storedDimension = stored, queryDimension = vector.Length },
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(repo.Search(vector, body!.TopK));
        });

        api.MapPost("/clip/index", static (ClipIndexRequest body, EmbeddingRepository repo, IEmbeddingProvider embedder) =>
        {
            var vector = body is null ? null : ResolveVector(body.Vector, body.Text, embedder);
            if (body is null || string.IsNullOrWhiteSpace(body.SourceType) || vector is null || vector.Length == 0)
            {
                return Results.BadRequest();
            }

            repo.Upsert(body.SourceType, body.RefId, body.Label, vector);
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/clip/{sourceType}/{refId:long}", static (string sourceType, long refId, EmbeddingRepository repo) =>
        {
            var vector = repo.ByRef(sourceType, refId);
            return vector is null ? Results.NotFound() : Results.Ok(new { Vector = vector });
        });

        api.MapDelete("/clip/{sourceType}/{refId:long}", static (string sourceType, long refId, EmbeddingRepository repo) =>
            repo.Delete(sourceType, refId) ? Results.Ok(new { ok = true }) : Results.NotFound());

        api.MapPost("/search/fuse", static (FuseRequest body) =>
        {
            if (body is null || body.Items is null || body.Items.Length == 0)
            {
                return Results.BadRequest();
            }

            var items = body.Items.Select(i => new FusionItem(i.Key, i.TextScore, i.VectorScore, i.TextWeight, i.VectorWeight));
            return Results.Ok(RankFusion.Fuse(items));
        });

        api.MapGet("/audio/event/{eventId:long}", static (long eventId, EventAudioRepository audio) =>
        {
            var rec = audio.GetByEvent(eventId);
            return rec is null ? Results.NotFound() : Results.Ok(new AudioItem(rec.EventId, rec.ChannelId, rec.StartedAtUtc, rec.DurationMs, rec.Mime, rec.Sha256));
        });

        api.MapPost("/audio", static (AudioUpsert body, EventAudioRepository audio) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Mime) || body.Bytes is null || body.Bytes.Length == 0)
            {
                return Results.BadRequest();
            }

            audio.Save(body.EventId, body.ChannelId, body.StartedAtUtc ?? DateTime.UtcNow, body.DurationMs, body.Mime, Convert.FromBase64String(body.Bytes));
            return Results.Ok(new { ok = true });
        });

        api.MapDelete("/audio/event/{eventId:long}", static (long eventId, EventAudioRepository audio) =>
            audio.Delete(eventId) ? Results.Ok(new { ok = true }) : Results.NotFound());

        api.MapGet("/audio", static (DateTime? from, DateTime? to, EventAudioRepository audio) =>
            Results.Ok(audio.QueryByTime(from?.ToUniversalTime(), to?.ToUniversalTime()).Select(a =>
                new AudioItem(a.EventId, a.ChannelId, a.StartedAtUtc, a.DurationMs, a.Mime, a.Sha256))));

        api.MapGet("/devices", static (DeviceRepository devices) =>
        {
            var list = devices.List();
            return Results.Ok(list.Select(d => new DeviceItem(d.Id, d.Name, d.Ip, d.Port, d.Vendor, d.Enabled)));
        });

        api.MapPost("/devices", static (DeviceUpsert body, DeviceRepository devices) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Ip))
            {
                return Results.BadRequest();
            }

            var id = devices.Add(body.Name, body.Ip, body.Port, body.Username ?? string.Empty, body.Password ?? string.Empty, body.Vendor ?? string.Empty, body.Actor ?? "system");
            return Results.Ok(new { id });
        });

        api.MapPut("/devices/{id:int}", static (int id, DeviceUpsert body, DeviceRepository devices) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Ip))
            {
                return Results.BadRequest();
            }

            if (!devices.Update(id, body.Name, body.Ip, body.Port, body.Vendor ?? string.Empty, body.Enabled, body.Actor ?? "system"))
            {
                return Results.NotFound();
            }

            // 只有在請求明確帶入帳密時才覆寫，否則局部更新（例如只改名稱）會把既有憑證清成空值，
            // 導致頻道之後完全無法連線。
            if (!string.IsNullOrEmpty(body.Username) || !string.IsNullOrEmpty(body.Password))
            {
                var current = devices.GetRtspCredentials(id);
                var username = string.IsNullOrEmpty(body.Username) ? current.Username : body.Username;
                var password = string.IsNullOrEmpty(body.Password) ? current.Password : body.Password;
                devices.SetRtspCredentials(id, username, password, body.Actor ?? "system");
            }

            return Results.Ok(new { ok = true });
        });

        api.MapDelete("/devices/{id:int}", static (int id, DeviceRepository devices) =>
        {
            devices.Delete(id);
            return Results.Ok(new { ok = true });
        });
    }

    private sealed record DeviceItem(int Id, string Name, string Ip, int Port, string Vendor, bool Enabled);
    private sealed record DeviceUpsert(string Name, string Ip, int Port, string? Username, string? Password, string? Vendor, bool Enabled, string? Actor);

    private sealed record ClipSearchRequest(float[]? Vector, string? Text, int TopK = 20);
    private sealed record ClipIndexRequest(string? SourceType, long RefId, string? Label, float[]? Vector, string? Text);

    /// <summary>
    /// 解析要用的向量（§14.1 #8）：優先採用呼叫端帶來的向量；未帶向量但給了文字時，交由
    /// <see cref="IEmbeddingProvider"/> 就地編碼。兩者皆無（或編碼結果為零向量）回 null。
    /// </summary>
    private static float[]? ResolveVector(float[]? vector, string? text, IEmbeddingProvider embedder)
    {
        if (vector is { Length: > 0 })
        {
            return vector;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var embedded = embedder.Embed(text);
        return embedded.Any(static x => x != 0f) ? embedded.ToArray() : null;
    }

    private sealed record FuseRequest(FuseItemBody[] Items);
    private sealed record FuseItemBody(string Key, double TextScore, double? VectorScore, double TextWeight = 1.0, double VectorWeight = 1.0);
    private sealed record SemanticSearchRequest(string? Q, float[]? Vector, DateTime? From, DateTime? To, int? Limit = 20);
    private sealed record AudioItem(long EventId, long ChannelId, DateTime StartedAtUtc, int DurationMs, string Mime, string Sha256);
    private sealed record AudioUpsert(long EventId, long ChannelId, DateTime? StartedAtUtc, int DurationMs, string? Mime, string? Bytes);

    private sealed record InputAck(bool Acknowledged);

    private static AlarmBoardItem ToBoard(AlarmBoardRow r) =>
        new(r.EventId, r.ChannelId, r.EventType, SqliteStore.Iso(r.StartUtc), r.Status, r.Priority,
            r.DueUtc is null ? null : SqliteStore.Iso(r.DueUtc.Value), r.Owner, r.AssignedTo, r.IsOverdue);

    private static RedactionItem ToRedaction(RedactionRegion r) =>
        new(r.Id, r.SourceType, r.RefId, r.ChannelId, SqliteStore.Iso(r.OccurredAtUtc), r.X, r.Y, r.Width, r.Height, r.Filled, SqliteStore.Iso(r.CreatedAtUtc));

    /// <summary>本 Web API 的版本（與桌面 App 同一條 Directory.Build.props 版本；M295）。</summary>
    private static string ApiVersion() =>
        typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>把 <see cref="CentralNode"/> 轉成不含金鑰的 API 檢視（M295）。</summary>
    private static CentralNodeStatusResponse ToCentralNodeStatus(CentralNode n) =>
        new(
            n.Id,
            n.Name,
            n.BaseUrl,
            n.Enabled,
            n.NodeId,
            n.Version,
            n.Channels,
            n.Online,
            n.LastSeenUtc is null ? null : SqliteStore.Iso(n.LastSeenUtc.Value),
            n.LastError);

    private static IReadOnlyList<ShareItem> ToShares(IReadOnlyList<ShareLinkRecord> records) => records.Select(ToShare).ToList();

    private static ShareItem ToShare(ShareLinkRecord s)
    {
        var expired = DateTime.TryParse(s.ExpiresAt, out var expires) && expires <= DateTime.UtcNow;
        return new(s.Id, s.Token, s.Kind, s.ResourcePath, s.Label, s.CreatedBy, s.ExpiresAt, s.MaxUses, s.UseCount, s.Revoked,
            !s.Revoked && !expired && (s.MaxUses <= 0 || s.UseCount < s.MaxUses));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AlertRuleItem ToRule(AlertRule r) =>
        new(r.Id, r.Name, r.EventType, r.ChannelId, r.Keyword, r.Channels, r.Enabled, r.MatchEventTypes, r.FrameMinutes, r.MinEventsInWindow);

    private static IReadOnlyList<LegalHoldItem> ToHolds(IReadOnlyList<LegalHoldRecord> records) =>
        records
            .Select(h => new LegalHoldItem(
                h.Id,
                h.ChannelId,
                SqliteStore.Iso(h.FromUtc),
                SqliteStore.Iso(h.ToUtc),
                h.Reason,
                h.CreatedBy,
                SqliteStore.Iso(h.CreatedAtUtc),
                h.RevokedAtUtc is null ? null : SqliteStore.Iso(h.RevokedAtUtc.Value),
                h.RevokedBy,
                h.RevokedReason,
                h.RevokedAtUtc is null))
            .ToList();

    private static IReadOnlyList<AccountListItem> Accounts(UserRepository users)
    {
        var now = DateTime.UtcNow;
        return users.ListUsers()
            .Select(u => new AccountListItem(
                u.Id,
                u.Username,
                u.Role,
                u.DisplayName,
                u.Enabled,
                u.LockedUntil is { Length: > 0 } lu && SqliteStore.FromIso(lu) > now))
            .ToList();
    }

    private static async Task HandleAudit(
        HttpContext context,
        AuditLogRepository audit,
        string? category,
        string? actor,
        string? action,
        DateTime? from,
        DateTime? to,
        int? limit)
    {
        var query = new AuditLogQuery
        {
            Category = category,
            Actor = actor,
            Action = action,
            FromUtc = from,
            ToUtc = to,
            Limit = Math.Clamp(limit ?? 100, 1, 500),
            Offset = 0,
        };
        if (from is { } f && to is { } t && f >= t)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "from 必須早於 to" });
            return;
        }

        var items = audit.List(query);
        await context.Response.WriteAsJsonAsync(new Paged<AuditLogEntry>(items, items.Count));
    }

    private static async Task HandleAuditExport(
        HttpContext context,
        AuditLogRepository audit,
        string? category,
        string? actor,
        string? action,
        DateTime? from,
        DateTime? to)
    {
        if (from is { } f && to is { } t && f >= t)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "from 必須早於 to" });
            return;
        }

        var rows = audit.List(new AuditLogQuery
        {
            Category = category,
            Actor = actor,
            Action = action,
            FromUtc = from,
            ToUtc = to,
        });

        context.Response.ContentType = "text/csv; charset=utf-8";
        context.Response.Headers.ContentDisposition = $"attachment; filename=audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetPreamble());
        await context.Response.WriteAsync(AuditLogCsv.Render(rows));
    }

    private static async Task HandleDailyReport(
        HttpContext context,
        ReportRepository reports,
        DateTime? from,
        DateTime? to)
    {
        var fromUtc = from ?? DateTime.UtcNow.Date;
        var toUtc = to ?? DateTime.UtcNow.Date.AddDays(1);
        if (fromUtc >= toUtc)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "from 必須早於 to" });
            return;
        }

        var response = new DailyReportResponse(
            reports.ListRecordingSummary(fromUtc, toUtc),
            reports.ListCapacityTrend(fromUtc, toUtc),
            reports.GetDisconnectCount(fromUtc, toUtc),
            reports.ListAiEventSummary(fromUtc, toUtc));
        await context.Response.WriteAsJsonAsync(response);
    }

    /// <summary>Turns repository guard exceptions into clean 400 responses.</summary>
    public static async Task ErrorFilter(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Playback timeline for a channel day window (M119, section 14.3 playback REST):
    /// normalized recording bars, event markers and gap stats for the SPA timeline band.
    /// </summary>
    /// <summary>
    /// Raw segment ledger for a stream/time window (M120, section 14.3 playback REST):
    /// lets the SPA build playback/HLS-style URLs straight from recorded files.
    /// </summary>
    /// <remarks>
    /// M239：<c>file_path</c> 是主機上的絕對路徑，遠程端沒有任何用途（主控台不會顯示它），
    /// 只會洩漏資料目錄配置，所以回應刻意不帶這個欄位——要取影片請走
    /// <c>/api/stream/segment/{id}/…</c>，由伺服器自己用索引裡的路徑讀檔。
    /// </remarks>
    private static async Task HandleRecordingSegments(
        HttpContext context,
        SegmentRepository segments,
        SegmentIndexReconciler reconciler,
        int channelId,
        string stream,
        DateTime from,
        DateTime to)
    {
        var fromUtc = Utc(from);
        var toUtc = Utc(to);
        if (toUtc <= fromUtc)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "時段上限不可早於下限");
            return;
        }

        // 上限與播放清單一致。分段長度可設定（`RecordingSegmentPolicy`，預設 60 秒、最短 5 秒），
        // 24 小時最短會到 17,280 筆、預設值 1,440 筆；沒有這個上限時一個「查最近一個月」
        // 的請求會撈出數十萬筆，整包序列化成 JSON 會吃掉幾百 MB——遠端主控台一個查詢就能讓
        // 服務記憶體暴增，而且使用者其實只會用到最後幾筆。
        if (toUtc - fromUtc > MaxPlaylistWindow)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "單次查詢區段最多 24 小時，請縮小時段");
            return;
        }

        // 查詢時對帳（與桌面回放同一條路徑，見 SegmentIndexReconciler）：使用者把錄影磁碟上的
        // 檔案刪掉之後，索引不會自己知道；不清單先驗一次，遠端主控台就會一直列出「有錄影」的時段，
        // 點下去才 404。每次查詢多幾千次 stat，遠小於回應體積本身；被標成 missing 的段之後
        // 連 stat 都不會再做（狀態已不是 final）。
        //
        // 先走 ListPlayableByRange（只取 final）而非 ListByRange：清單只該列可播放的錄影，
        // tmp／corrupt／retired 混進 JSON 會讓主控台顯示點不開的列，口徑與播放清單端點一致。
        var items = reconciler
            .VerifyExisting(segments.ListPlayableByRange(channelId, stream, fromUtc, toUtc))
            .Playable;
        var publicItems = items.Select(static s => new SegmentListItem(
            s.Id, s.ChannelId, s.Stream, s.StartUtc, s.EndUtc, s.SizeBytes, s.DurationSec, s.Format, s.Status, s.Sha256)).ToArray();
        await context.Response.WriteAsJsonAsync(new Paged<SegmentListItem>(publicItems, publicItems.Length));
    }

    /// <summary>跨機回放代理：向子節點取播放清單並改寫片段網址（§14.1 #10，M301）。</summary>
    private static async Task HandleCentralPlaylist(
        HttpContext context,
        CentralNodeRepository repo,
        ICentralNodeClient client,
        long nodeId,
        int channelId,
        string? stream,
        DateTime from,
        DateTime to,
        CancellationToken cancellation)
    {
        var node = repo.List().FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "找不到節點");
            return;
        }

        if (channelId <= 0 || string.IsNullOrWhiteSpace(stream))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "頻道與串流必填");
            return;
        }

        var fromUtc = Utc(from);
        var toUtc = Utc(to);
        if (toUtc <= fromUtc)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "時段上限不可早於下限");
            return;
        }

        if (toUtc - fromUtc > MaxPlaylistWindow)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "單次播放清單最多 24 小時，請縮小時段");
            return;
        }

        var path = $"api/stream/{channelId}/playlist.m3u8?stream={Uri.EscapeDataString(stream)}"
            + $"&from={Uri.EscapeDataString(fromUtc.ToString("O"))}&to={Uri.EscapeDataString(toUtc.ToString("O"))}";

        var resource = await FetchFromChild(context, client, node, path, cancellation).ConfigureAwait(false);
        if (resource is null)
        {
            return;
        }

        if (resource.StatusCode != StatusCodes.Status200OK)
        {
            await WriteChildFailure(context, resource);
            return;
        }

        context.Response.ContentType = HlsPlaylist.ContentType;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(
            CentralPlayback.Rewrite(Encoding.UTF8.GetString(resource.Body), nodeId), cancellation);
    }

    /// <summary>跨機回放代理：轉發子節點的初始化段（§14.1 #10，M301）。</summary>
    private static Task HandleCentralSegmentInit(
        HttpContext context,
        CentralNodeRepository repo,
        ICentralNodeClient client,
        long nodeId,
        long segmentId,
        CancellationToken cancellation) =>
        ProxyCentralSegment(context, repo, client, nodeId, segmentId, init: true, cancellation);

    /// <summary>跨機回放代理：轉發子節點的媒體段（§14.1 #10，M301）。</summary>
    private static Task HandleCentralSegmentMedia(
        HttpContext context,
        CentralNodeRepository repo,
        ICentralNodeClient client,
        long nodeId,
        long segmentId,
        CancellationToken cancellation) =>
        ProxyCentralSegment(context, repo, client, nodeId, segmentId, init: false, cancellation);

    private static async Task ProxyCentralSegment(
        HttpContext context,
        CentralNodeRepository repo,
        ICentralNodeClient client,
        long nodeId,
        long segmentId,
        bool init,
        CancellationToken cancellation)
    {
        var node = repo.List().FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "找不到節點");
            return;
        }

        var leaf = init ? "init.mp4" : "media.m4s";
        var resource = await FetchFromChild(
            context, client, node, $"api/stream/segment/{segmentId}/{leaf}", cancellation).ConfigureAwait(false);
        if (resource is null)
        {
            return;
        }

        if (resource.StatusCode != StatusCodes.Status200OK)
        {
            await WriteChildFailure(context, resource);
            return;
        }

        context.Response.ContentType = init ? HlsPlaylist.InitContentType : HlsPlaylist.MediaContentType;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.Body.WriteAsync(resource.Body, cancellation);
    }

    /// <summary>代理由子節點取資源；連線層失敗一律轉 502，讓上層錯誤與子節點狀態碼分開。</summary>
    private static async Task<CentralNodeResource?> FetchFromChild(
        HttpContext context,
        ICentralNodeClient client,
        CentralNode node,
        string relativePath,
        CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(node.BaseUrl))
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "節點尚未設定位址");
            return null;
        }

        try
        {
            return await client
                .FetchResourceAsync(node.BaseUrl, node.ApiKey, relativePath, cancellation)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 使用者取消（關掉播放器）不是子節點壞掉，不該記成 502。
            throw;
        }
        catch (Exception)
        {
            await WriteError(context, StatusCodes.Status502BadGateway, "無法連線子節點");
            return null;
        }
    }

    /// <summary>子節點非 200 時轉譯：404 原樣傳（此時段沒錄影），其餘視為上游錯誤回 502。</summary>
    private static Task WriteChildFailure(HttpContext context, CentralNodeResource resource) =>
        resource.StatusCode == StatusCodes.Status404NotFound
            ? WriteError(context, StatusCodes.Status404NotFound, "子節點此時段沒有錄影")
            : WriteError(context, StatusCodes.Status502BadGateway, $"子節點回應 {resource.StatusCode}");

    /// <summary>HLS VOD 播放清單（§14.3 串流 P0）：遠程回放的唯一入口。</summary>
    private static async Task HandleStreamPlaylist(
        HttpContext context,
        SegmentRepository segments,
        SegmentIndexReconciler reconciler,
        int channelId,
        string stream,
        DateTime from,
        DateTime to)
    {
        var fromUtc = Utc(from);
        var toUtc = Utc(to);
        if (toUtc <= fromUtc)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "時段上限不可早於下限");
            return;
        }

        if (toUtc - fromUtc > MaxPlaylistWindow)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "單次播放清單最多 24 小時，請縮小時段");
            return;
        }

        // 只把真的還在磁碟上的 final 段寫進清單：tmp（正在寫）、corrupt、missing 一旦出現在
        // #EXTINF 序列裡，播放器會在該片請求 init/media 時吃 404 而整段卡住——清單說有、
        // 播放卻停在中間，比整段沒有錄影更難判斷。查閱時對帳同時修正索引（同上）。
        // ListPlayableByRange 已只取 final，對帳再把檔案不存在的剔除。
        var items = reconciler
            .VerifyExisting(segments.ListPlayableByRange(channelId, stream, fromUtc, toUtc))
            .Playable;
        if (items.Count == 0)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "此時段沒有錄影");
            return;
        }

        context.Response.ContentType = HlsPlaylist.ContentType;
        context.Response.Headers.CacheControl = "private, no-cache";
        await context.Response.WriteAsync(HlsPlaylist.Build(items));
    }

    /// <summary>EXT-X-MAP 初始化段（ftyp＋moov）。</summary>
    private static async Task HandleSegmentInit(
        HttpContext context,
        SegmentRepository segments,
        RecordedSegmentPolicy policy,
        long id)
    {
        if (!TryResolveSegment(context, segments, policy, id, out var segment, out var failure))
        {
            await failure!();
            return;
        }

        var bytes = await ReadPrefixAsync(segment!.FilePath, InitPrefixBytes);
        context.Response.ContentType = HlsPlaylist.InitContentType;
        context.Response.Headers.CacheControl = "private, no-cache";
        await context.Response.Body.WriteAsync(bytes.AsMemory(0, Fmp4Splitter.Split(bytes).InitLength));
    }

    /// <summary>EXTINF 媒體段（moof/mdat）；HLS 的 seek 是片段層級，不需要片段內的 byte range。</summary>
    private static async Task HandleSegmentMedia(
        HttpContext context,
        SegmentRepository segments,
        RecordedSegmentPolicy policy,
        long id)
    {
        if (!TryResolveSegment(context, segments, policy, id, out var segment, out var failure))
        {
            await failure!();
            return;
        }

        var parts = Fmp4Splitter.Split(await ReadPrefixAsync(segment!.FilePath, InitPrefixBytes));
        var info = new FileInfo(segment.FilePath);
        if (info.Length <= parts.InitLength)
        {
            await WriteError(context, StatusCodes.Status404NotFound, "錄影分段沒有媒體內容");
            return;
        }

        context.Response.ContentType = HlsPlaylist.MediaContentType;
        context.Response.Headers.CacheControl = "private, no-cache";
        await using var stream = info.OpenRead();
        stream.Seek(parts.InitLength, SeekOrigin.Begin);
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    /// <summary>
    /// 分段解析的共同防線：必須是已完成的區段、路徑必須在錄影根目錄內、檔案必須還在。
    /// 呼叫端只給編號，路徑一律取自索引。
    /// </summary>
    private static bool TryResolveSegment(
        HttpContext context,
        SegmentRepository segments,
        RecordedSegmentPolicy policy,
        long id,
        out SegmentRecord? segment,
        out Func<Task>? failure)
    {
        segment = segments.Get(id);
        if (segment is null || segment.Status != SegmentStatus.Final)
        {
            failure = () => WriteError(context, StatusCodes.Status404NotFound, "找不到該錄影分段");
            return false;
        }

        if (!policy.IsRecordedSegmentPath(segment.FilePath))
        {
            var path = segment.FilePath;
            failure = () => WriteError(context, StatusCodes.Status403Forbidden, policy.DenyMessage(path));
            return false;
        }

        if (!File.Exists(segment.FilePath))
        {
            failure = () => WriteError(context, StatusCodes.Status404NotFound, "錄影檔已不存在");
            return false;
        }

        failure = null;
        return true;
    }

    private static async Task<byte[]> ReadPrefixAsync(string path, int maxBytes)
    {
        var length = (int)Math.Min(new FileInfo(path).Length, maxBytes);
        var buffer = new byte[length];
        await using var stream = File.OpenRead(path);
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static async Task WriteError(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }

    private static async Task HandleRecordingTimeline(
        HttpContext context,
        SegmentRepository segments,
        SegmentIndexReconciler reconciler,
        AlarmEventRepository events,
        int channelId,
        string stream,
        DateTime day)
    {
        var dayStart = Utc(day);
        var dayEnd = dayStart.AddSeconds(PlaybackTimeline.DaySeconds);

        // 時間軸欄位＝「點得下去看得到畫面」，口徑必須與播放清單一致：先對帳再只取 final，
        // 否則已刪掉的錄影在時間軸上仍是一整條藍 bar，點下去卻沒有片可播。
        var segs = reconciler
            .VerifyExisting(segments.ListPlayableByRange(channelId, stream, dayStart, dayEnd))
            .Playable;
        var evs = events.ListByRange(channelId, dayStart, dayEnd);
        var timeline = PlaybackTimelineBuilder.Build(segs, evs, dayStart);
        await context.Response.WriteAsJsonAsync(timeline);
    }

    private static async Task HandleSmartwallBoard(
        HttpContext context,
        AlarmEventRepository events,
        AlarmTriageRepository triage)
    {
        var now = DateTime.UtcNow;
        var boardEvents = new List<SmartwallBoardEvent>();
        foreach (var e in events.ListByRange(null, now - SmartwallTimings.MosaicKeepLast, now))
        {
            boardEvents.Add(new SmartwallBoardEvent(
                e.ChannelId,
                e.EventType,
                triage.Get(e.Id)?.Priority ?? "normal",
                e.StartUtc,
                0));
        }

        await context.Response.WriteAsJsonAsync(SmartwallAlertBoard.Snapshot(boardEvents, now, 64));
    }

    private static async Task HandlePos(
        HttpContext context,
        POSEventRepository pos,
        int? deviceId,
        DateTime? from,
        DateTime? to,
        int? limit)
    {
        if (from is null || to is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "from/to are required" });
            return;
        }

        var items = pos.Query(deviceId, Utc(from.Value), Utc(to.Value), Math.Clamp(limit ?? 200, 1, 1000));
        await context.Response.WriteAsJsonAsync(new Paged<POSEvent>(items, items.Count));
    }

    /// <summary>
    /// 匯入單筆 POS 交易（M93／§14.7 #8）。以 <see cref="POSEventRepository.InsertDedupe"/>
    /// 在 <see cref="ReconWindow"/> 內去重：重複交易回既有 Id 且 <c>Inserted=false</c>。
    /// 收銀機重送不應產生重複交易，否則對帳（recon）會多報 Unmatched／Duplicates。
    /// </summary>
    private static async Task HandlePosIngest(HttpContext context, POSEventRepository pos, PosEventRequest body)
    {
        if (body.DeviceId <= 0 ||
            string.IsNullOrWhiteSpace(body.RegisterId) ||
            string.IsNullOrWhiteSpace(body.TransactionNo))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "deviceId > 0, registerId and transactionNo are required" });
            return;
        }

        var (id, inserted) = pos.InsertDedupe(
            body.DeviceId,
            body.RegisterId.Trim(),
            body.TransactionNo.Trim(),
            body.AmountCents,
            Utc(body.OccurredAtUtc),
            ReconWindow);

        context.Response.StatusCode = inserted ? StatusCodes.Status201Created : StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new PosIngestResult(id, inserted));
    }

    /// <summary>
    /// 匯入單筆門禁刷卡事件（M92／§14.7 #8）。direction 正規化為 In／Out；同一刷卡於
    /// <see cref="DoorDedupeWindow"/> 內重送回既有 Id 且 <c>Inserted=false</c>。
    /// </summary>
    private static async Task HandleDoorIngest(HttpContext context, DoorEventRepository doors, DoorEventRequest body)
    {
        var direction = body.Direction?.Trim();
        var normalized = direction is not null && direction.Equals("in", StringComparison.OrdinalIgnoreCase) ? "In"
            : direction is not null && direction.Equals("out", StringComparison.OrdinalIgnoreCase) ? "Out"
            : null;

        if (body.DeviceId <= 0 || body.DoorId <= 0 || string.IsNullOrWhiteSpace(body.CardId) || normalized is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "deviceId > 0, doorId > 0, cardId and direction (In/Out) are required" });
            return;
        }

        var (id, inserted) = doors.InsertDedupe(
            body.DeviceId,
            body.DoorId,
            body.CardId.Trim(),
            normalized,
            body.Granted,
            body.Reason?.Trim() ?? string.Empty,
            Utc(body.OccurredAtUtc),
            DoorDedupeWindow);

        context.Response.StatusCode = inserted ? StatusCodes.Status201Created : StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new DoorEventIngestResult(id, inserted));
    }

    /// <summary>
    /// POS 異常彙總（§14.7 #8，M282）：由交易後設資料（金額離群／短時連發）推得異常清單。
    /// 純統計、不需影像；供桌面與瀏覽器視覺化「哪裡不太對」。
    /// </summary>
    private static async Task HandlePosAnomalies(
        HttpContext context,
        POSEventRepository pos,
        int? deviceId,
        DateTime? from,
        DateTime? to)
    {
        var txns = pos.Query(
            deviceId,
            from is { } f ? Utc(f) : null,
            to is { } t ? Utc(t) : null);
        await context.Response.WriteAsJsonAsync(PosAnomalyDetector.Analyze(txns));
    }

    private static async Task HandlePosRecon(
        HttpContext context,
        POSEventRepository pos,
        AlarmEventRepository events,
        int deviceId,
        string registerId,
        DateTime from,
        DateTime to)
    {
        var txns = pos.QueryByRegister(deviceId, registerId, Utc(from), Utc(to));
        var candidates = events.ListByRange(null, Utc(from), Utc(to));
        var summary = PosReconciliation.Compute(txns, candidates, static e => e.StartUtc, ReconWindow);
        await context.Response.WriteAsJsonAsync(
            new PosReconResult(summary.Total, summary.Matched, summary.Unmatched, summary.Duplicates));
    }

    private static async Task HandleForensicSearch(
        HttpContext context,
        UnifiedEventSearch search,
        string q,
        DateTime? from,
        DateTime? to,
        int? limit)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "q is required" });
            return;
        }

        var hits = search.Search(q, from is { } f ? Utc(f) : null, to is { } t ? Utc(t) : null,
            ForensicSource.All, Math.Clamp(limit ?? 20, 1, 200));
        await context.Response.WriteAsJsonAsync(new Paged<ForensicSearchHit>(hits, hits.Count));
    }

    /// <summary>
    /// 法證語意融合搜尋（§14.7 #7）：文字查詢與／或查詢向量兩路命中以 <see cref="RankFusion"/> 合併後回傳。
    /// 查詢向量由外部模型提供；未帶向量但已接上 <see cref="IEmbeddingProvider"/> 時就地編碼（§14.1 #8），
    /// 否則退化為全文檢索。
    /// </summary>
    private static async Task HandleSemanticSearch(
        HttpContext context,
        ForensicSemanticSearch search,
        SemanticSearchRequest? body)
    {
        if (body is null ||
            (string.IsNullOrWhiteSpace(body.Q) && (body.Vector is null || body.Vector.Length == 0)))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "q or vector is required" });
            return;
        }

        var hits = search.Search(
            body.Q ?? string.Empty,
            body.Vector,
            body.From is { } f ? Utc(f) : null,
            body.To is { } t ? Utc(t) : null,
            ForensicSource.All,
            Math.Clamp(body.Limit ?? 20, 1, 200));
        await context.Response.WriteAsJsonAsync(new Paged<SemanticSearchHit>(hits, hits.Count));
    }

    private static async Task HandleEvents(
        HttpContext context,
        AlarmEventRepository r,
        DateTime from,
        DateTime to,
        int? channelId,
        string? eventType,
        string? status,
        string? keyword,
        int? offset,
        int? limit)
    {
        var q = new AlarmEventRepository.QueryArgs
        {
            ChannelId = channelId,
            EventType = eventType,
            Status = status,
            Keyword = keyword,
            FromUtc = Utc(from),
            ToUtc = Utc(to),
            Offset = Math.Max(0, offset ?? 0),
            Limit = Math.Clamp(limit ?? 200, 1, 500),
        };

        await context.Response.WriteAsJsonAsync(
            new Paged<AlarmEventRecord>(r.ListByQuery(q), r.CountByQuery(q)));
    }

    /// <summary>
    /// Live alert stream (M118, section 14.3): after the API-key handshake, the client
    /// receives JSON <see cref="AlertUpdate"/> frames each time an alarm is acknowledged,
    /// dispositioned or triaged. Subscription is registered before the socket is accepted
    /// so no update published after ConnectAsync returns can be missed.
    /// </summary>
    private static async Task HandleAlertStream(HttpContext context, AlertBroadcastHub hub)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var subscription = hub.Subscribe(out var reader);
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var token = context.RequestAborted;

        try
        {
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                if (!(await reader.WaitToReadAsync(token)))
                {
                    break;
                }

                while (reader.TryRead(out var update))
                {
                    var json = JsonSerializer.Serialize(update);
                    await socket.SendAsync(
                        Encoding.UTF8.GetBytes(json),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        token);
                }
            }
        }
        catch (WebSocketException)
        {
            // Client went away; drain subscription via using.
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}