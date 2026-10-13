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
