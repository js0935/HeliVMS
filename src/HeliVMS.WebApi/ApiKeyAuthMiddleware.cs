using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HeliVMS.WebApi;

/// <summary>
/// Protects every /api/* route (M117): a bearer key configured through
/// HELIVMS_API_KEY, compared in constant time. /api/health stays open.
/// 未設定 HELIVMS_API_KEY 時僅 Development 環境沿用 dev 金鑰；其餘環境一律拒絕（fail-closed）。
/// 設 HELIVMS_API_KEY_DISABLED=1 可完全關閉驗證（見該常數），僅供受信任且未對外開放的本機環境。
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    public const string DefaultDevKey = "helivms-dev-key";
    public const string ApiKeyConfigKey = "HELIVMS_API_KEY";
    public const string DisabledConfigKey = "HELIVMS_API_KEY_DISABLED";
    private const string Scheme = "Bearer ";

    private const int MaxFailuresPerWindow = 20;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BlockFor = TimeSpan.FromSeconds(60);

    private readonly RequestDelegate _next;
    private readonly byte[]? _expected;
    private readonly bool _disabled;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;
    private readonly ConcurrentDictionary<string, FailureBucket> _failures = new(StringComparer.Ordinal);

    public ApiKeyAuthMiddleware(RequestDelegate next, IConfiguration config, IHostEnvironment env, ILogger<ApiKeyAuthMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        // 完全關閉驗證的逃生門（HELIVMS_API_KEY_DISABLED=1）。刻意做成顯式開關而非預設行為：
        // 預設仍 fail-closed，否則一個部署設定打錯就等於把管理介面裸露給任何能連到埠的人。
        if (IsTruthy(config[DisabledConfigKey]))
        {
            _disabled = true;
            _logger.LogWarning(
                "{ConfigKey} 已設定：/api/* 完全不驗證 Bearer 金鑰。僅應在受信任且未對外開放的本機環境使用。",
                DisabledConfigKey);
            return;
        }

        var configured = config[ApiKeyConfigKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _expected = System.Text.Encoding.UTF8.GetBytes(configured.Trim());
            return;
        }

        if (env.IsDevelopment())
        {
            _expected = System.Text.Encoding.UTF8.GetBytes(DefaultDevKey);
            return;
        }

        _expected = null;
        _logger.LogWarning(
            "{ConfigKey} 未設定且環境為 {Environment}：/api/* 將全部拒絕（health 除外）。",
            ApiKeyConfigKey,
            env.EnvironmentName);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        if (_disabled || !isApi || path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (_expected is null)
        {
            await Unauthorized(context);
            return;
        }

        if (TryBlock(context))
        {
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

        if (provided is null || !Matches(provided))
        {
            RecordFailure(context);
            await Unauthorized(context);
            return;
        }

        await _next(context);
    }

    private bool Matches(string provided)
    {
        var candidate = System.Text.Encoding.UTF8.GetBytes(provided.Trim());
        return candidate.Length == _expected!.Length &&
               CryptographicOperations.FixedTimeEquals(candidate, _expected);
    }

    /// <summary>認可 1/true/yes/on（不分大小寫、可含空白），其餘（含空值）視為未設定。</summary>
    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return trimmed == "1"
            || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>同一來源於時窗內失敗過多時直接回 429；無來源位址（如測試）不計入。</summary>
    private bool TryBlock(HttpContext context)
    {
        var key = BucketKey(context);
        if (key is null)
        {
            return false;
        }

        if (!_failures.TryGetValue(key, out var window) || window.StartedUtc + FailureWindow <= DateTime.UtcNow)
        {
            return false;
        }

        if (window.Count < MaxFailuresPerWindow)
        {
            return false;
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter =
            ((int)BlockFor.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private void RecordFailure(HttpContext context)
    {
        var key = BucketKey(context);
        if (key is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        _failures.AddOrUpdate(
            key,
            _ => new FailureBucket(1, now),
            (_, current) => current.StartedUtc + FailureWindow <= now
                ? new FailureBucket(1, now)
                : new FailureBucket(current.Count + 1, current.StartedUtc));
    }

    private static string? BucketKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.MapToIPv4().ToString();

    private static async Task Unauthorized(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
    }

    private readonly record struct FailureBucket(int Count, DateTime StartedUtc);
}
