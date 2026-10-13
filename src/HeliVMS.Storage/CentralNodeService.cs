using System.Globalization;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>
/// 中央站的節點聚合服務（§14.1 #10，M295）：逐一探測啟用中的子節點，並把結果寫回 <c>central_nodes</c>。
///
/// 探測是「盡力而為」：任何失敗（連不上、逾時、金鑰錯誤、回應壞掉）都只記為離線並保留錯誤訊息，
/// 不讓單一節點拖垮整輪聚合。時間來源以 <see cref="TimeProvider"/> 注入，測試可固定時鐘驗證 last_seen。
/// </summary>
public sealed class CentralNodeService
{
    private readonly CentralNodeRepository _repository;
    private readonly ICentralNodeClient _client;
    private readonly TimeProvider _clock;

    public CentralNodeService(CentralNodeRepository repository, ICentralNodeClient client, TimeProvider? clock = null)
    {
        _repository = repository;
        _client = client;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>列出全部節點（含停用者），附帶最近一次探測結果。</summary>
    public IReadOnlyList<CentralNode> List() => _repository.List();

    /// <summary>依序探測所有啟用中的節點，回傳更新後的完整清單。</summary>
    public async Task<IReadOnlyList<CentralNode>> ProbeAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var node in _repository.ListEnabled())
        {
            await ProbeAsync(node, cancellationToken).ConfigureAwait(false);
        }

        return _repository.List();
    }

    /// <summary>探測單一節點並回傳更新後的內容。</summary>
    public async Task<CentralNode> ProbeAsync(CentralNode node, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        try
        {
            var status = await _client.FetchAsync(node.BaseUrl, node.ApiKey, cancellationToken).ConfigureAwait(false);
            _repository.RecordProbe(node.Id, online: true, atUtc: now, result: status, error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 呼叫端主動取消（例如關閉服務）不該被記成節點離線；逾時則不然——
            // HttpClient 逾時擲出的取消其 token 並未取消，會落到下一個 catch 正確記為離線。
            throw;
        }
        catch (Exception ex)
        {
            _repository.RecordProbe(node.Id, online: false, atUtc: _clock.GetUtcNow().UtcDateTime, result: null, error: ex.Message);
        }

        return _repository.List().First(n => n.Id == node.Id);
    }

    /// <summary>
    /// 逐台取回啟用中子節點的頻道清單並彙整（§14.1 #10，M302）。
    ///
    /// 與探測同樣是「盡力而為」：單一節點連不上、逾時、回非 200 或 JSON 壞掉都只跳過該節點，
    /// 不讓一台子節點壞掉就整個清單拿不到——跨機面板寧可少列幾台，也不該整片空白。
    /// 停用節點與未設位址者不查。呼叫端主動取消向上拋（與 <see cref="ProbeAsync"/> 同語意），
    /// 其餘例外一律視為該節點失敗。
    /// </summary>
    public async Task<IReadOnlyList<CentralChannel>> ListChannelsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CentralChannel>();
        foreach (var node in _repository.ListEnabled())
        {
            if (string.IsNullOrWhiteSpace(node.BaseUrl))
            {
                continue;
            }

            CentralNodeResource resource;
            try
            {
                resource = await _client
                    .FetchResourceAsync(node.BaseUrl, node.ApiKey, "api/channels", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            if (resource.StatusCode != 200)
            {
                continue;
            }

            result.AddRange(ParseChannels(resource.Body, node));
        }

        return result;
    }

    /// <summary>
    /// 逐台取回啟用中子節點的近期警報並彙整（§14.1 #10，M303）。
    ///
    /// 每台子節點各取最多 <paramref name="take"/> 筆，合併後以「逾期優先、再依起始時間新到舊」
    /// 排序，最後截到 <paramref name="take"/> 筆——跨機看板最關心「現在正在燒的那幾筆」，
    /// 而不是把所有節點的歷史全倒出來。單台節點失敗（連不上、逾時、非 200、JSON 壞掉）
    /// 只跳過該台，與 <see cref="ListChannelsAsync"/> 同語意；呼叫端主動取消才往上拋。
    /// </summary>
    public async Task<IReadOnlyList<CentralAlarm>> ListAlarmsAsync(
        int take = 100, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(take, 1, 500);
        var result = new List<CentralAlarm>();
        foreach (var node in _repository.ListEnabled())
        {
            if (string.IsNullOrWhiteSpace(node.BaseUrl))
            {
                continue;
            }

            CentralNodeResource resource;
            try
            {
                resource = await _client
                    .FetchResourceAsync(node.BaseUrl, node.ApiKey, $"api/alarm-board?take={limit}", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            if (resource.StatusCode != 200)
            {
                continue;
            }

            result.AddRange(ParseAlarms(resource.Body, node));
        }

        return result
            .OrderByDescending(a => a.Overdue)
            .ThenByDescending(a => a.StartUtc)
            .ThenBy(a => a.NodeId)
            .ThenByDescending(a => a.EventId)
            .Take(limit)
            .ToArray();
    }

    /// <summary>
    /// 解析子節點 <c>/api/alarm-board</c> 的 JSON 陣列。與 <see cref="ParseChannels"/> 同樣寬容：
    /// 非物件、缺 <c>eventId</c> 或識別非正的條目一律跳過，絕不讓一台子節點的回應拖垮整輪聚合。
    /// </summary>
    private static IReadOnlyList<CentralAlarm> ParseAlarms(byte[] body, CentralNode node)
    {
        if (body.Length == 0)
        {
            return Array.Empty<CentralAlarm>();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CentralAlarm>();
            }

            var list = new List<CentralAlarm>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var eventId = ReadLong(element, "eventId");
                if (eventId is null or <= 0)
                {
                    continue;
                }

                list.Add(new CentralAlarm(
                    node.Id,
                    node.Name,
                    eventId.Value,
                    ReadInt(element, "channelId") ?? 0,
                    ReadString(element, "eventType") ?? string.Empty,
                    ReadDate(element, "startUtc") ?? DateTime.MinValue,
                    ReadString(element, "status") ?? "pending",
                    ReadString(element, "priority") ?? AlarmPriority.Normal,
                    ReadDate(element, "dueUtc"),
                    ReadString(element, "owner"),
                    ReadString(element, "assignedTo"),
                    ReadBool(element, "overdue") ?? false));
            }

            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<CentralAlarm>();
        }
    }

    /// <summary>
    /// 解析子節點 <c>/api/channels</c> 的 JSON 陣列。任何非陣列、缺 <c>id</c>、型別不對或壞字串
    /// 都跳過/回空，絕不讓一台子節點的回應把整輪聚合拖垮。
    /// </summary>
    private static IReadOnlyList<CentralChannel> ParseChannels(byte[] body, CentralNode node)
    {
        if (body.Length == 0)
        {
            return Array.Empty<CentralChannel>();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CentralChannel>();
            }

            var list = new List<CentralChannel>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadInt(element, "id");
                if (id is null or <= 0)
                {
                    continue;
                }

                list.Add(new CentralChannel(
                    node.Id,
                    node.Name,
                    id.Value,
                    ReadString(element, "name") is { Length: > 0 } name ? name : $"ch{id}",
                    ReadBool(element, "enabled") ?? true));
            }

            return list;
        }
        catch (JsonException)
        {
            return Array.Empty<CentralChannel>();
        }
    }

    // 子節點是同一套 ASP.NET 序列化（camelCase），但為了容忍第三方或未來變更，一律不分大小寫比對。
    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : null;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : null;
    }

    /// <summary>
    /// 解析子節點序列化的時間字串（ISO 8601）。子節點以 UTC 輸出，但為容忍帶位移的格式，
    /// 一律調整回 UTC；無法解析時回 <c>null</c>，由呼叫端決定預設值。
    /// </summary>
    private static DateTime? ReadDate(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value is { ValueKind: JsonValueKind.String } v
            && DateTime.TryParse(
                v.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed)
            ? parsed
            : null;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    }

    private static bool? ReadBool(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
