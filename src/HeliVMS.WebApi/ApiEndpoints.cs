using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
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
    public sealed record Paged<T>(IReadOnlyList<T> Items, int Count);
    public sealed record HealthResponse(string Status, string Database, int Channels);
    public sealed record AckRequest(bool Acknowledged);
    public sealed record TriageRequest(string Priority, DateTime? DueUtc, string? Owner);
    public sealed record DispositionRequest(string Status, string? AssignedTo, string? Note);
    public sealed record PosReconResult(int Total, int Matched, int Unmatched, int Duplicates);
    public sealed record LoginRequest(string Username, string Password);
    public sealed record AccountUpsertRequest(string Username, string Password, string Role, string? DisplayName);
    public sealed record AccountPatchRequest(string? Role, bool? Enabled, string? DisplayName);
    public sealed record AccountListItem(int Id, string Username, string Role, string? DisplayName, bool Enabled, bool Locked);
    public sealed record ConfigResponse(bool AuthEnabled, int LockoutThreshold, int LockoutMinutes, int RecordingRetentionDays, double RecordingWatermarkGb, int AlarmRetentionDays);
    public sealed record ConfigRequest(bool? AuthEnabled, int? LockoutThreshold, int? LockoutMinutes, int? RecordingRetentionDays, double? RecordingWatermarkGb, int? AlarmRetentionDays);
    public sealed record UsageResponse(long Bytes, double Gb, int RetentionDays, double WatermarkGb, int AlarmRetentionDays);
    public sealed record RetentionRunResult(int AgePurged, int WatermarkPurged, long BytesFreed, long AlarmPurged);
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
    public sealed record DailyReportResponse(
        IReadOnlyList<RecordingSummaryRow> Recording,
        IReadOnlyList<CapacityTrendRow> Capacity,
        long Disconnects,
        IReadOnlyList<AiEventCountRow> Events);
    public sealed record ScheduleBody(long Id, int ChannelId, int DaysMask, int StartMinute, int EndMinute, bool Enabled);
    public sealed record ScheduleUpsertRequest(int ChannelId, int DaysMask, int StartMinute, int EndMinute, bool Enabled);
    public sealed record PatrolStepBody(string PresetName, int DwellSeconds);
    public sealed record PatrolUpsertRequest(string Name, int ChannelId, bool Enabled, string? WindowStart, string? WindowEnd, IReadOnlyList<PatrolStepBody>? Steps);

    private static readonly TimeSpan ReconWindow = TimeSpan.FromSeconds(10);

    public static void MapAll(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", static (SqliteStore store, ChannelRepository channels) =>
            Results.Ok(new HealthResponse("ok", "ok", channels.List().Count)));

        api.MapGet("/channels", static (ChannelRepository r) => Results.Ok(r.List()));

        api.MapGet("/events", HandleEvents);

        api.MapGet("/events/search", HandleForensicSearch);

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

        api.MapGet("/pos/recon", HandlePosRecon);

        api.MapGet("/smartwall/board", HandleSmartwallBoard);

        api.MapGet("/recording/timeline", HandleRecordingTimeline);

        api.MapGet("/recording/segments", HandleRecordingSegments);

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

        api.MapPost("/accounts/authenticate", static (LoginRequest body, AuthService auth) =>
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

            return Results.Ok(new { role = result.Role, displayName = result.DisplayName });
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
                (int)settings.GetDoubleOrDefault(RetentionService.AlarmDaysKey, 365))));

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

            return Results.Ok(new ConfigResponse(
                settings.GetOrDefault("auth.enabled", "0") == "1",
                (int)settings.GetDoubleOrDefault("auth.lockout.threshold", 5),
                (int)settings.GetDoubleOrDefault("auth.lockout.minutes", 5),
                (int)settings.GetDoubleOrDefault(RetentionService.DaysKey, 30),
                settings.GetDoubleOrDefault(RetentionService.WatermarkGbKey, 0),
                (int)settings.GetDoubleOrDefault(RetentionService.AlarmDaysKey, 365)));
        });

        api.MapGet("/config/usage", static (RetentionService retention) =>
            Results.Ok(new UsageResponse(
                retention.UsageBytes,
                retention.UsageBytes / 1073741824.0,
                retention.RetentionDays,
                retention.WatermarkGb,
                retention.AlarmRetentionDays)));

        api.MapPost("/retention/run", static (RetentionService retention) =>
        {
            var result = retention.RunOnce(DateTime.UtcNow);
            return Results.Ok(new RetentionRunResult(result.AgePurged, result.WatermarkPurged, result.BytesFreed, result.AlarmPurged));
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

        api.MapPost("/evidence/package", static (EvidencePackageRequest body, EvidenceManifestRepository manifests) =>
        {
            if (string.IsNullOrWhiteSpace(body.BundleName))
            {
                return Results.BadRequest(new { error = "包名必填" });
            }

            if (body.Files is null or { Count: 0 })
            {
                return Results.BadRequest(new { error = "至少一個來源檔案" });
            }

            var missing = body.Files.FirstOrDefault(f => !File.Exists(f));
            if (missing is not null)
            {
                return Results.BadRequest(new { error = $"來源檔不存在：{missing}" });
            }

            EvidencePackager.BundleManifest manifest;
            try
            {
                manifest = EvidencePackager.BuildManifest(body.BundleName, body.Files);
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var outDir = Path.Combine(Path.GetTempPath(), "helivms-evidence");
            Directory.CreateDirectory(outDir);
            var bundlePath = Path.Combine(outDir, $"{body.BundleName}-{manifest.BundleId:N}.evp");
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

        api.MapPost("/evidence/verify", static (EvidenceVerifyRequest body) =>
        {
            if (string.IsNullOrWhiteSpace(body.BundlePath) || !File.Exists(body.BundlePath))
            {
                return Results.BadRequest(new { error = "包檔不存在" });
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

        api.MapPost("/backup/run", static (BackupRunRequest body, BackupService backup) =>
        {
            if (string.IsNullOrWhiteSpace(body.SourceRoot) || string.IsNullOrWhiteSpace(body.TargetRoot))
            {
                return Results.BadRequest(new { error = "來源與目標目錄必填" });
            }

            if (!Directory.Exists(body.SourceRoot))
            {
                return Results.BadRequest(new { error = "來源目錄不存在" });
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

        api.MapPost("/shares", static (ShareRequest body, ShareLinkRepository shares) =>
        {
            if (!ShareKind.IsValid(body.Kind) || string.IsNullOrWhiteSpace(body.ResourcePath))
            {
                return Results.BadRequest(new { error = "類型（segment/snapshot/evidence）與資源路徑必填" });
            }

            var maxUses = body.MaxUses < 0 ? 0 : body.MaxUses;
            var id = shares.Add(ShareToken.Create(), body.Kind, body.ResourcePath, NullIfBlank(body.Label), null, DateTime.UtcNow, null, body.ExpiresAt, maxUses);
            var record = shares.Get(id)!;
            return Results.Ok(ToShare(record));
        });

        api.MapPut("/shares/{id:int}/revoke", static (int id, ShareLinkRepository shares) =>
        {
            if (shares.Get(id) is null)
            {
                return Results.NotFound();
            }

            shares.SetRevoked(id, true);
            return Results.Ok(new { ok = true });
        });

        api.MapDelete("/shares/{id:int}", static (int id, ShareLinkRepository shares) =>
        {
            if (shares.Get(id) is null)
            {
                return Results.NotFound();
            }

            shares.Delete(id);
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
    }

    private static RedactionItem ToRedaction(RedactionRegion r) =>
        new(r.Id, r.SourceType, r.RefId, r.ChannelId, SqliteStore.Iso(r.OccurredAtUtc), r.X, r.Y, r.Width, r.Height, r.Filled, SqliteStore.Iso(r.CreatedAtUtc));

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

        var sb = new StringBuilder();
        sb.Append("id,occurred_at,actor,action,category,target_type,target_id,detail\n");
        foreach (var r in rows)
        {
            sb.Append(r.Id);
            sb.Append(',');
            sb.Append(CsvCell(SqliteStore.Iso(r.OccurredAtUtc)));
            sb.Append(',');
            sb.Append(CsvCell(r.Actor));
            sb.Append(',');
            sb.Append(CsvCell(r.Action));
            sb.Append(',');
            sb.Append(CsvCell(r.Category));
            sb.Append(',');
            sb.Append(CsvCell(r.TargetType));
            sb.Append(',');
            sb.Append(CsvCell(r.TargetId?.ToString()));
            sb.Append(',');
            sb.Append(CsvCell(r.Detail));
            sb.Append('\n');
        }

        context.Response.ContentType = "text/csv; charset=utf-8";
        context.Response.Headers.ContentDisposition = $"attachment; filename=audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetPreamble());
        await context.Response.WriteAsync(sb.ToString());
    }

    private static string CsvCell(string? value)
    {
        if (value is null)
        {
            return "";
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
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
    private static async Task HandleRecordingSegments(
        HttpContext context,
        SegmentRepository segments,
        int channelId,
        string stream,
        DateTime from,
        DateTime to)
    {
        var items = segments.ListByRange(channelId, stream, Utc(from), Utc(to));
        await context.Response.WriteAsJsonAsync(new Paged<SegmentRecord>(items, items.Count));
    }

    private static async Task HandleRecordingTimeline(
        HttpContext context,
        SegmentRepository segments,
        AlarmEventRepository events,
        int channelId,
        string stream,
        DateTime day)
    {
        var dayStart = Utc(day);
        var dayEnd = dayStart.AddSeconds(PlaybackTimeline.DaySeconds);
        var segs = segments.ListByRange(channelId, stream, dayStart, dayEnd);
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