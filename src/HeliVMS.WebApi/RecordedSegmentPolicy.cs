using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>
/// 錄影分段檔的主機路徑政策（§14.3 串流）：遠程只能拿到
/// <c>segments.file_path</c> 指向的檔案，且該路徑必須位於錄影根目錄之內。
/// </summary>
/// <remarks>
/// 呼叫端只給分段編號，路徑一律取自索引，這是關鍵——API 不接受任何呼叫端提供的路徑。
/// 根目錄解析與桌面端一致（<c>HELIVMS_RECORDINGS_ROOT</c> → <c>HELIVMS_DATA</c>＋recordings
/// → 預設資料目錄），解析不出來就整個拒絕（fail closed）。
/// </remarks>
public sealed class RecordedSegmentPolicy
{
    /// <summary>錄影根目錄設定鍵，與 <see cref="RetentionService"/> 共用同一個。</summary>
    public const string RecordingsRootConfigKey = RetentionService.RecordingsRootKey;

    public const string DataRootConfigKey = "HELIVMS_DATA";
    public const string RecordingsDirName = "recordings";
    public const string DefaultDataRoot = @"C:\HeliVMSData";

    private readonly string[] _roots;

    public RecordedSegmentPolicy(IConfiguration config)
        : this(Resolve(config))
    {
    }

    public RecordedSegmentPolicy(IEnumerable<string>? roots)
    {
        _roots = (roots ?? [])
            .Where(static r => !string.IsNullOrWhiteSpace(r))
            .Select(static r => Normalize(r))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>錄影檔是否位於任一錄影根目錄之內；沒有可解析的根目錄時永為 false。</summary>
    public bool IsRecordedSegmentPath(string? path)
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

    public void EnsureRecordedSegmentPath(string? path)
    {
        if (!IsRecordedSegmentPath(path))
        {
            throw new UnauthorizedAccessException(DenyMessage(path ?? string.Empty));
        }
    }

    public string DenyMessage(string path) => _roots.Length == 0
        ? $"無法解析錄影根目錄（{RetentionService.RecordingsRootKey}／{DataRootConfigKey} 都未設定）：{path}"
        : $"路徑不在錄影根目錄內：{path}";

    /// <summary>依設定解析錄影根目錄；無設定時回傳空集合（呼叫端一律拒絕）。</summary>
    public static IReadOnlyList<string> Resolve(IConfiguration config)
    {
        var explicitRoot = config[RetentionService.RecordingsRootKey];
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return [explicitRoot];
        }

        var dataRoot = config[DataRootConfigKey];
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return [Path.Combine(DefaultDataRoot, RecordingsDirName)];
        }

        return [Path.Combine(dataRoot, RecordingsDirName)];
    }

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
