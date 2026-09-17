using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeliVMS.Shared.Models;

namespace HeliVMS.Alarms;

/// <summary>
/// Web Push 發送器（RFC 8030 提交端）：POST 事件 JSON 至訂閱端點，帶 VAPID
/// JWT（RFC 8292；ES256 自簽 P-256，金鑰對由 <see cref="GenerateKeyPair"/> 產生）。
/// 不依賴任何第三方 NuGet；傳送失敗回 false（重試在 <see cref="NotificationService"/> 層）。
/// </summary>
public sealed class PushNotifier
{
    private readonly HttpClient _http;

    public PushNotifier(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>送出一則事件至 push 端點。需 PushEnabled＋endpoint＋VAPID 私鑰。</summary>
    public async Task<bool> SendAsync(NotificationSettings cfg, AlarmEventRecord record)
    {
        var endpoint = cfg.PushEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(cfg.PushPrivateKey))
        {
            return false;
        }

        try
        {
            // VAPID audience＝endpoint 的 origin（scheme://host[:port]）
            var audience = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
            var publicKey = ResolvePublicKey(cfg.PushPublicKey, cfg.PushPrivateKey);
            var jwt = CreateVapidJwt(cfg.PushPrivateKey, publicKey, audience);

            var payload = JsonSerializer.Serialize(new
            {
                channel_id = record.ChannelId,
                event_type = record.EventType,
                start_utc = record.StartUtc.ToString("o"),
                detail = record.Detail,
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("TTL", "60");
            req.Headers.TryAddWithoutValidation("Urgency", "normal");
            req.Headers.TryAddWithoutValidation("Authorization", $"vapid t={jwt}, k={publicKey}");
            req.Headers.TryAddWithoutValidation("Content-Encoding", "identity");

            using var resp = await _http.SendAsync(req);
            return resp.StatusCode is System.Net.HttpStatusCode.Created
                or System.Net.HttpStatusCode.OK
                or System.Net.HttpStatusCode.NoContent;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>產生 P-256 VAPID 金鑰對（公鑰為 65 位元組 uncompressed point 之 base64url，RFC 8292）。</summary>
    public static (string PublicKey, string PrivateKey) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(true);

        var point = new byte[65];
        point[0] = 0x04;
        Buffer.BlockCopy(p.Q!.X!, 0, point, 1, 32);
        Buffer.BlockCopy(p.Q.Y!, 0, point, 33, 32);

        return (B64UrlEncode(point), Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
    }

    /// <summary>以 VAPID 私鑰簽署 RFC 8292 JWT（ES256；header+claims+raw R||S 簽名）。</summary>
    public static string CreateVapidJwt(string privateKeyB64, string publicKeyB64, string audience)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);

        var header = B64UrlEncode(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds();
        var claims = B64UrlEncode(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { aud = audience, exp, sub = "mailto:helivms@localhost" })));

        var signingInput = $"{header}.{claims}";
        var sig = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"{signingInput}.{B64UrlEncode(sig)}";
    }

    /// <summary>從私鑰導出對應的公鑰 point（設定缺 public_key 時補齊）。</summary>
    internal static string ResolvePublicKey(string? publicKeyB64, string privateKeyB64)
    {
        if (!string.IsNullOrWhiteSpace(publicKeyB64))
        {
            return publicKeyB64;
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
        var p = ecdsa.ExportParameters(false);
        var point = new byte[65];
        point[0] = 0x04;
        Buffer.BlockCopy(p.Q!.X!, 0, point, 1, 32);
        Buffer.BlockCopy(p.Q.Y!, 0, point, 33, 32);
        return B64UrlEncode(point);
    }

    private static string B64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}