using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>
/// 主機檔案存取政策（§14.7 #4）：evidence／shares／backup 只能觸碰 <c>HELIVMS_ALLOWED_ROOTS</c>
/// 允許的根目錄；未設定允許根目錄時一律拒絕（fail closed），避免 API 成為任意讀寫檔案的跳板。
/// 證據包輸出／驗證另外允許伺服器自有的 <see cref="EvidenceDir"/>。
/// </summary>
public sealed class PathAccessPolicy
{
    public const string RootsConfigKey = "HELIVMS_ALLOWED_ROOTS";
    public const string EvidenceDirName = "helivms-evidence";
    public const int MaxBundleNameLength = 64;

    private static readonly char[] Separators = [';', '\n', '\r'];

    private readonly string[] _roots;

    public PathAccessPolicy(IConfiguration config)
        : this(Split(config[RootsConfigKey]))
    {
    }

    public PathAccessPolicy(IEnumerable<string>? roots)
    {
        _roots = (roots ?? [])
            .Where(static r => !string.IsNullOrWhiteSpace(r))
            .Select(static r => Normalize(r))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        EvidenceDir = Path.Combine(Path.GetTempPath(), EvidenceDirName);
    }

    /// <summary>已正規化的允許根目錄（絕對路徑）。</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>伺服器自有的證據包輸出目錄，不受允許根目錄限制。</summary>
    public string EvidenceDir { get; }

    /// <summary>路徑是否位於任一允許根目錄之內；未設定根目錄時永為 false。</summary>
    public bool IsAllowed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var root in _roots)
        {
            if (SharePath.IsWithinRoot(root, path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>證據包讀寫：允許根目錄或伺服器證據目錄（<paramref name="path"/> 必須已存在於其中之一）。</summary>
    public bool IsEvidencePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return SharePath.IsWithinRoot(EvidenceDir, path) || IsAllowed(path);
    }

    /// <summary>未設定允許根目錄時的統一錯誤訊息。</summary>
    public string DenyMessage(string path) => _roots.Length == 0
        ? $"路徑不在允許根目錄內（尚未設定 {RootsConfigKey}）：{path}"
        : $"路徑不在允許根目錄內：{path}";

    public void EnsureAllowed(string? path)
    {
        if (!IsAllowed(path))
        {
            throw new UnauthorizedAccessException(DenyMessage(path ?? string.Empty));
        }
    }

    public void EnsureEvidencePath(string? path)
    {
        if (!IsEvidencePath(path))
        {
            throw new UnauthorizedAccessException(DenyMessage(path ?? string.Empty));
        }
    }

    /// <summary>
    /// 檢查證據包名稱：僅允許檔名安全字元，且不得為相對路徑／保留名稱，
    /// 避免 <c>BundleName</c> 影響輸出路徑造成目錄穿越。
    /// </summary>
    public static string ValidateBundleName(string? bundleName)
    {
        if (string.IsNullOrWhiteSpace(bundleName))
        {
            throw new ArgumentException("包名必填", nameof(bundleName));
        }

        if (bundleName.Length > MaxBundleNameLength)
        {
            throw new ArgumentException($"包名長度不可超過 {MaxBundleNameLength} 字元", nameof(bundleName));
        }

        if (bundleName is "." or "..")
        {
            throw new ArgumentException("包名不可為相對路徑", nameof(bundleName));
        }

        foreach (var c in bundleName)
        {
            if (c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                throw new ArgumentException("包名不可包含路徑分隔符或控制字元", nameof(bundleName));
            }
        }

        return bundleName;
    }

    private static IEnumerable<string> Split(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Normalize(string root)
    {
        try
        {
            return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
