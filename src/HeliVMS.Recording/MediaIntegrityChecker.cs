using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>單一區段的完整性判定。</summary>
internal enum IntegrityVerdict
{
    /// <summary>檔案存在且可解析出視訊串流。</summary>
    Ok,

    /// <summary>索引有列、檔案卻不存在（被外部刪除、磁碟掉線等）。</summary>
    Missing,

    /// <summary>檔案在，但 ffprobe 判為無視訊串流或無法解析。</summary>
    Corrupt,

    /// <summary>探測本身無法完成（ffprobe 不在 PATH、檔案被鎖住、逾時），無法判定。</summary>
    Unknown,
}

/// <summary>
/// 一輪完整性抽查的結果。欄位刻意只有計數：自我檢查的產物是「有沒有問題」與稽核紀錄，
/// 不是可操作的清單——真正要處理某一段時，看稽核日誌即可。
/// </summary>
public readonly record struct IntegrityCheckReport(int Checked, int Corrupt, int Missing)
{
    /// <summary>本輪是否查到任何可疑區段（供呼叫端決定是否記警告）。</summary>
    public bool AnySuspicious => Corrupt > 0 || Missing > 0;
}

/// <summary>
/// 錄影完整性自我檢查（§11.1 寫盤保護、§15.7 定期以 ffprobe 抽查已封檔區段）。
///
/// <para>
/// 只觀察與記錄，**不改狀態、不刪檔**：保留清理的用量統計與汰除候選都以
/// <c>status='final'</c> 為準，若把查到的壞段標成 <c>corrupt</c>，它會同時退出用量會計
/// （<see cref="SegmentRepository.GetTotalUsage"/>）與汰除候選，變成永遠刪不掉的孤兒——
/// 磁碟照樣佔著、用量卻少算，正是使用者看不出磁碟為何快滿的那種症狀。
/// 因此這裡只寫稽核（<c>recording.integrity</c>），實際汰除仍交給保留清理。
/// </para>
/// <para>
/// 分頁進行：每輪只抽查 <see cref="DefaultBatchSize"/> 段「尚未檢查」的 final 段，查過就標記，
/// 下一輪才會往前推進。抽查是取樣而非全驗，成本因此可預期且可調。
/// </para>
/// </summary>
public sealed class MediaIntegrityChecker
{
    /// <summary>單輪預設抽查段數：夠小到每圈是毫秒級，又能持續覆蓋全部歷史區段。</summary>
    public const int DefaultBatchSize = 5;

    /// <summary>單段 ffprobe 探測逾時；逾時視為無法判定，不當成損壞。</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly SegmentRepository _repo;
    private readonly Func<string, bool?> _isPlayable;
    private readonly AuditLogRepository? _audit;

    /// <param name="repo">區段索引。</param>
    /// <param name="isPlayable">
    /// 判定某路徑是否可解析出視訊串流；回 <c>null</c> 表示無法判定（探測失敗）。
    /// 預設用真實 ffprobe；測試注入替身以脫離外部進程。
    /// </param>
    /// <param name="audit">稽核日誌；<c>null</c> 時不寫稽核（供不掛稽核的呼叫端）。</param>
    public MediaIntegrityChecker(
        SegmentRepository repo,
        Func<string, bool?>? isPlayable = null,
        AuditLogRepository? audit = null)
    {
        _repo = repo;
        _isPlayable = isPlayable ?? DefaultIsPlayable;
        _audit = audit;
    }

    /// <summary>抽查最多 <paramref name="batchSize"/> 段尚未檢查的 final 區段。</summary>
    public IntegrityCheckReport Run(DateTime nowUtc, int batchSize = DefaultBatchSize)
    {
        var checkedCount = 0;
        var corrupt = 0;
        var missing = 0;

        foreach (var (id, channelId, filePath) in _repo.ListFinalUnchecked(batchSize))
        {
            checkedCount++;
            var verdict = Verify(filePath);

            switch (verdict)
            {
                case IntegrityVerdict.Corrupt:
                    corrupt++;
                    break;
                case IntegrityVerdict.Missing:
                    missing++;
                    break;
            }

            if (verdict is IntegrityVerdict.Corrupt or IntegrityVerdict.Missing)
            {
                _audit?.Record(
                    "system",
                    "recording.integrity",
                    AuditCategories.Recording,
                    targetType: "segment",
                    targetId: id,
                    detail: $"channel={channelId} verdict={verdict} path={filePath}");
            }

            // 無論判定結果（含無法判定）都標記已檢查：否則探測失敗的段每輪都會被重取，
            // 抽查永遠卡在同一批最舊的段上，較新的段永遠輪不到。
            _repo.MarkChecked(id, nowUtc);
        }

        return new IntegrityCheckReport(checkedCount, corrupt, missing);
    }

    private IntegrityVerdict Verify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return IntegrityVerdict.Missing;
        }

        try
        {
            return _isPlayable(filePath) switch
            {
                true => IntegrityVerdict.Ok,
                false => IntegrityVerdict.Corrupt,
                null => IntegrityVerdict.Unknown,
            };
        }
        catch (Exception)
        {
            // 驗證器本身擲出例外不該等同「檔案壞了」：把無法判定與損壞分開，
            // 否則一支沒裝 ffprobe 的機器會把每一段都記成損壞。
            return IntegrityVerdict.Unknown;
        }
    }

    /// <summary>
    /// 以 ffprobe 對本地檔案抽取第一個視訊串流：能解析出 <c>streams</c> 即視為完好。
    /// 與 <see cref="HeliVMS.Media.StreamProbe"/> 不同，這裡不帶 <c>-rtsp_transport</c>——
    /// 對象是本地 mp4 檔，帶上 RTSP 參數會讓部分 ffmpeg 版本直接報錯。
    /// </summary>
    private static bool? DefaultIsPlayable(string filePath)
    {
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffprobe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            foreach (var arg in new[]
                     {
                         "-v", "error",
                         "-select_streams", "v:0",
                         "-show_entries", "stream=codec_type",
                         "-of", "json",
                         filePath,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            // 同時抽乾 stdout/stderr，避免任一管線填滿使進程阻塞。
            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                TryKill(proc);
                return null;
            }

            var output = stdout.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
            {
                // ffprobe 明確以非零結束 → 檔案無法解析。
                return false;
            }

            using var doc = JsonDocument.Parse(output);
            return doc.RootElement.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.GetArrayLength() > 0;
        }
        catch (Exception)
        {
            // 無法啟動 ffprobe（不在 PATH）、輸出非 JSON 等 → 無法判定。
            return null;
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 進程已結束或無權限；後續不再等待即可。
        }
    }
}
