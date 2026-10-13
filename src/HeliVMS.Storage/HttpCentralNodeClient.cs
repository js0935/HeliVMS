using System.Net.Http.Headers;
using System.Text.Json;
using HeliVMS.Shared.Models;

namespace HeliVMS.Storage;

/// <summary>
/// 以真實 HTTP 呼叫子節點 <c>GET /api/node</c>（§14.1 #10，M295）。
///
/// <see cref="HttpMessageHandler"/> 由外部注入：正式環境給 <c>SocketsHttpHandler</c>，
/// 測試給回固定回應的假 handler，因此「有沒有帶 Bearer 標頭、打哪個路徑、怎麼解析」都能離線驗證。
/// </summary>
public sealed class HttpCentralNodeClient : ICentralNodeClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <param name="handler">實際發出請求的處理器（測試注入假物件）。</param>
    /// <param name="timeout">單次探測逾時；未指定為 5 秒。子節點無回應時不應讓聚合端卡住。</param>
    public HttpCentralNodeClient(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        };
    }

    public async Task<NodeStatus> FetchAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(baseUrl, "api/node"));
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<NodeStatus>(json, JsonOptions)
            ?? throw new JsonException("節點回應為空或格式不符。");
    }

    public async Task<CentralNodeResource> FetchResourceAsync(
        string baseUrl, string apiKey, string relativePath, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(baseUrl, relativePath));
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // 刻意不呼叫 EnsureSuccessStatusCode：代理要自己分辨 404 與其他失敗。
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new CentralNodeResource(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            body);
    }

    private static Uri BuildUri(string baseUrl, string relativePath)
    {
        // 相容「有無尾端斜線」兩種設定：先把 base 統一成目錄，再相對組出路徑，
        // 否則 new Uri("http://host:80", "api/node") 會把最後一段當檔名而吃掉。
        var normalized = baseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(normalized), relativePath);
    }

    public void Dispose() => _http.Dispose();
}
