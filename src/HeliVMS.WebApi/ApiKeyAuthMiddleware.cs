using System.Security.Cryptography;

namespace HeliVMS.WebApi;

/// <summary>
/// Protects every /api/* route (M117): a bearer key configured through
/// HELIVMS_API_KEY (default dev key), compared in constant time. /api/health stays open.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    public const string DefaultDevKey = "helivms-dev-key";
    private const string Scheme = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly byte[] _expected;

    public ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _expected = System.Text.Encoding.UTF8.GetBytes(config["HELIVMS_API_KEY"] ?? DefaultDevKey);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        if (!isApi || path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        string? provided = null;
        if (header.StartsWith(Scheme, StringComparison.Ordinal))
        {
            provided = header[Scheme.Length..];
        }
        else if (context.WebSockets.IsWebSocketRequest &&
                 context.Request.Query.TryGetValue("key", out var queryKey))
        {
            provided = queryKey.ToString();
        }

        if (provided is null)
        {
            await Unauthorized(context);
            return;
        }

        var candidate = System.Text.Encoding.UTF8.GetBytes(provided);
        if (candidate.Length != _expected.Length ||
            !CryptographicOperations.FixedTimeEquals(candidate, _expected))
        {
            await Unauthorized(context);
            return;
        }

        await _next(context);
    }

    private static async Task Unauthorized(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
    }
}