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
}
