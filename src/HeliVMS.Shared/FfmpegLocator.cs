namespace HeliVMS.Shared;

/// <summary>
/// 解析 ffmpeg／ffprobe 執行檔位置。
/// </summary>
/// <remarks>
/// 解析順序固定為：<b>安裝目錄下的 <c>ffmpeg\</c>（內建）→ 環境變數 → PATH</b>。
/// <para>
/// 為什麼內建優先：本專案依賴特定 ffmpeg 行為（<c>-f rtp</c> muxer、H.264
/// <c>packetization-mode</c>、<c>v360</c> filter），使用者 PATH 上的版本可能不同。
/// 安裝時附一份釘版即可「裝好即用」，現場不必另外安裝 ffmpeg；環境變數則保留給
/// 進階／測試場景覆寫（沒有內建檔時才生效）。
/// </para>
/// <para>
/// PATH 回退刻意只回執行檔名（<c>"ffmpeg"</c>）而非解析絕對路徑：讓 <see cref="System.Diagnostics.Process"/>
/// 依系統的 PATH／PATHEXT 解析，行為與舊版一致。
/// </para>
/// </remarks>
public static class FfmpegLocator
{
    /// <summary>安裝目錄下存放內建執行檔的子資料夾名稱。</summary>
    public const string DirectoryName = "ffmpeg";

    /// <summary>覆寫 ffmpeg 路徑的環境變數（內建檔不存在時生效）。</summary>
    public const string FfmpegVariable = "HELIVMS_FFMPEG";

    /// <summary>覆寫 ffprobe 路徑的環境變數（內建檔不存在時生效）。</summary>
    public const string FfprobeVariable = "HELIVMS_FFPROBE";

    private const string FfmpegName = "ffmpeg";
    private const string FfprobeName = "ffprobe";

    /// <summary>ffmpeg 執行檔：優先內建，其次 <see cref="FfmpegVariable"/>，最後 PATH。</summary>
    public static string Ffmpeg => Resolve(FfmpegName);

    /// <summary>ffprobe 執行檔：優先內建，其次 <see cref="FfprobeVariable"/>，最後 PATH。</summary>
    public static string Ffprobe => Resolve(FfprobeName);

    /// <summary>
    /// 解析指定執行檔位置。
    /// </summary>
    /// <param name="executable">執行檔名（不含副檔名），例如 <c>ffmpeg</c>／<c>ffprobe</c>。</param>
    public static string Resolve(string executable)
        => Resolve(executable, baseDirectory: null, readVariable: null);

    /// <summary>
    /// 解析指定執行檔位置；可注入安裝目錄與環境變數讀取（供測試決定性驗證）。
    /// </summary>
    /// <param name="executable">執行檔名（不含副檔名）。</param>
    /// <param name="baseDirectory">安裝（基底）目錄；<c>null</c> 時用 <see cref="AppContext.BaseDirectory"/>。</param>
    /// <param name="readVariable">環境變數讀取委派；<c>null</c> 時用 <see cref="Environment.GetEnvironmentVariable(string)"/>。</param>
    public static string Resolve(
        string executable,
        string? baseDirectory,
        Func<string, string?>? readVariable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        var root = baseDirectory ?? AppContext.BaseDirectory;
        var read = readVariable ?? Environment.GetEnvironmentVariable;

        var fileName = OperatingSystem.IsWindows() ? executable + ".exe" : executable;
        var bundled = Path.Combine(root, DirectoryName, fileName);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var variable = executable.Equals(FfprobeName, StringComparison.OrdinalIgnoreCase)
            ? FfprobeVariable
            : FfmpegVariable;
        var configured = read(variable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        return executable;
    }
}
