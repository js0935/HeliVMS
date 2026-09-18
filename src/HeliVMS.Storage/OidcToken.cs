using System.Text;
using System.Text.Json;

namespace HeliVMS.Storage;

/// <summary>JWT 標頭（M50）。</summary>
public sealed record OidcHeader(string Alg, string? Kid, string? Typ);

/// <summary>
/// 已解析的 JWT（M50）：保留原始三段（供簽章輸入）與酬載 JSON。
/// </summary>
public sealed record OidcToken(
    string HeaderSegment,
    string PayloadSegment,
    string SignatureSegment,
    OidcHeader Header,
    JsonElement Payload)
{
    /// <summary>簽章輸入（<c>header.payload</c>，ASCII）。</summary>
    public string SigningInput => $"{HeaderSegment}.{PayloadSegment}";

    public static OidcToken Parse(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new FormatException("權杖為空");
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 3)
        {
            throw new FormatException("權杖需為三段式 JWT");
        }

        JsonElement headerRoot;
        JsonElement payloadRoot;
        try
        {
            using var headerDoc = JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url.Decode(parts[0])));
            headerRoot = headerDoc.RootElement.Clone();
            using var payloadDoc = JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url.Decode(parts[1])));
            payloadRoot = payloadDoc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw new FormatException($"權杖內容無法解析（{ex.Message}）");
        }

        var header = new OidcHeader(
            GetString(headerRoot, "alg") ?? string.Empty,
            GetString(headerRoot, "kid"),
            GetString(headerRoot, "typ"));

        return new OidcToken(parts[0], parts[1], parts[2], header, payloadRoot);
    }

    internal static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null,
            }
            : null;
}
