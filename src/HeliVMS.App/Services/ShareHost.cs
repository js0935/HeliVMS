using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// 輕量 HTTP 分享主機（M51，§14.7 #4）：僅綁定 loopback，提供
/// <c>GET /share/{token}</c> 下載、<c>GET /share/{token}/info</c> 摘要、<c>POST /share/{token}</c> 帶密碼。
/// 使用 <see cref="TcpListener"/> 以避免 HttpListener 的 URL ACL 需求。
/// </summary>
public sealed class ShareHost : IDisposable
{
    public const int DefaultPort = 8600;
    public const string EnabledKey = "share.enabled";
    public const string PortKey = "share.port";
    public const string BaseUrlKey = "share.base_url";

    private readonly ShareLinkService _links;
    private SettingsRepository? _settings;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _accept;

    public ShareHost(SqliteStore store)
    {
        _links = new ShareLinkService(store);
    }

    public int Port { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>解析設定中的對外基底 URL（未設定時以 localhost＋目前連接埠推導）。</summary>
    public static string ResolveBaseUrl(SettingsRepository settings, int port)
    {
        var url = settings.Get(BaseUrlKey);
        if (string.IsNullOrWhiteSpace(url))
        {
            url = $"http://localhost:{port}";
        }

        return url.TrimEnd('/');
    }

    public string ResolvedBaseUrl
    {
        get
        {
            var port = IsRunning ? Port : ReadPort();
            return _settings is null ? $"http://localhost:{port}" : ResolveBaseUrl(_settings, port);
        }
    }

    public string LinkFor(string token) => $"{ResolvedBaseUrl}/share/{token}";

    /// <summary>依設定啟用／停用分享服務，回傳狀態訊息。</summary>
    public string ApplySettings(SettingsRepository settings)
    {
        _settings = settings;
        var enabled = settings.Get(EnabledKey) == "1";
        if (!enabled)
        {
            Stop();
            return "分享服務：已停用";
        }

        var port = ReadPort();
        if (IsRunning && Port == port)
        {
            return $"分享服務：執行中（連接埠 {Port}）";
        }

        Stop();
        try
        {
            Start(port);
            return $"分享服務：執行中（連接埠 {Port}）";
        }
        catch (Exception ex)
        {
            return $"分享服務：啟動失敗（{ex.Message}）";
        }
    }

    private int ReadPort()
    {
        var raw = _settings?.Get(PortKey);
        return int.TryParse(raw, out var port) && port is > 0 and <= 65535 ? port : DefaultPort;
    }

    public void Start(int port)
    {
        if (IsRunning)
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        _accept = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        try
        {
            _accept?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _accept = null;
        IsRunning = false;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                if (request is null)
                {
                    return;
                }

                var response = Handle(request.Method, request.Path, request.Query, request.Body);
                await WriteResponseAsync(stream, response);
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private sealed record ShareRequest(string Method, string Path, string Query, string Body);

    private sealed record ShareResponse(int Status, string ContentType, byte[] Body, string? FileName = null);

    private ShareResponse Handle(string method, string path, string query, string body)
    {
        const string prefix = "/share/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Error(404, "找不到資源", ShareDeny.NotFound);
        }

        var segments = path[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 0 or > 2)
        {
            return Error(404, "找不到資源", ShareDeny.NotFound);
        }

        var token = Uri.UnescapeDataString(segments[0]);
        var wantsInfo = segments.Length == 2 && segments[1].Equals("info", StringComparison.OrdinalIgnoreCase);
        if (segments.Length == 2 && !wantsInfo)
        {
            return Error(404, "找不到資源", ShareDeny.NotFound);
        }

        var password = ParseForm(query, "password");
        if (method == "POST")
        {
            password = ParseForm(body, "password") ?? password;
        }

        var record = _links.GetByToken(token);
        var result = _links.Evaluate(record, DateTime.UtcNow, password);
        if (!result.Ok)
        {
            return Error(StatusFor(result.Reason), result.Error, result.Reason);
        }

        if (wantsInfo)
        {
            var info = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["kind"] = result.Kind,
                ["label"] = result.Label,
                ["size"] = new FileInfo(result.ResourcePath).Length,
            };

            return new ShareResponse(200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info)));
        }

        if (method is not "GET" and not "HEAD")
        {
            return Error(405, "方法不允許", ShareDeny.NotFound);
        }

        var bytes = File.ReadAllBytes(result.ResourcePath);
        _links.RecordUse(record!.Id, DateTime.UtcNow);
        var fileName = Path.GetFileName(result.ResourcePath);
        return new ShareResponse(200, "application/octet-stream", bytes, fileName);
    }

    private static int StatusFor(string reason) => reason switch
    {
        ShareDeny.Expired => 410,
        ShareDeny.Exhausted => 403,
        ShareDeny.Password => 401,
        _ => 404,
    };

    private static ShareResponse Error(int status, string message, string reason)
        => new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            ok = false,
            error = message,
            reason,
        })));

    private static string? ParseForm(string text, string key)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(pair[..idx].Replace('+', ' '));
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(idx + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }

    private static async Task<ShareRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        var headerEnd = -1;
        byte[] data = Array.Empty<byte>();

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                return null;
            }

            ms.Write(buffer, 0, read);
            data = ms.ToArray();
            headerEnd = FindHeaderEnd(data);
            if (data.Length > 256 * 1024)
            {
                return null;
            }
        }

        var headerText = Encoding.ASCII.GetString(data, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3)
        {
            return null;
        }

        var method = requestLine[0].ToUpperInvariant();
        var target = requestLine[1];
        var qIdx = target.IndexOf('?');
        var path = qIdx >= 0 ? target[..qIdx] : target;
        var query = qIdx >= 0 ? target[(qIdx + 1)..] : string.Empty;

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            if (line[..idx].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line[(idx + 1)..].Trim(), out var len))
            {
                contentLength = len;
            }
        }

        var bodyBytes = new byte[Math.Max(contentLength, 0)];
        var bodyStart = headerEnd + 4;
        var available = data.Length - bodyStart;
        var copied = Math.Clamp(available, 0, contentLength);
        if (copied > 0)
        {
            Array.Copy(data, bodyStart, bodyBytes, 0, copied);
        }

        var got = copied;
        while (got < contentLength)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                break;
            }

            var take = Math.Min(read, contentLength - got);
            Array.Copy(buffer, 0, bodyBytes, got, take);
            got += take;
        }

        var body = got > 0 ? Encoding.UTF8.GetString(bodyBytes, 0, got) : string.Empty;
        return new ShareRequest(method, path, query, body);
    }

    private static int FindHeaderEnd(byte[] data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, ShareResponse response)
    {
        var reason = response.Status switch
        {
            200 => "OK",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            410 => "Gone",
            _ => "Error",
        };

        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {response.Status} {reason}\r\n");
        sb.Append($"Content-Type: {response.ContentType}\r\n");
        sb.Append($"Content-Length: {response.Body.Length}\r\n");
        sb.Append("Connection: close\r\n");
        if (response.FileName is { Length: > 0 } name)
        {
            sb.Append($"Content-Disposition: attachment; filename=\"{name}\"\r\n");
        }

        sb.Append("\r\n");

        var header = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(header);
        if (response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body);
        }

        await stream.FlushAsync();
    }
}
