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
    public sealed record ConfigResponse(bool AuthEnabled, int LockoutThreshold, int LockoutMinutes);
    public sealed record ConfigRequest(bool? AuthEnabled, int? LockoutThreshold, int? LockoutMinutes);

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

        api.Map("/alerts/ws", HandleAlertStream);

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
                (int)settings.GetDoubleOrDefault("auth.lockout.minutes", 5))));

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

            return Results.Ok(new ConfigResponse(
                settings.GetOrDefault("auth.enabled", "0") == "1",
                (int)settings.GetDoubleOrDefault("auth.lockout.threshold", 5),
                (int)settings.GetDoubleOrDefault("auth.lockout.minutes", 5)));
        });
    }

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