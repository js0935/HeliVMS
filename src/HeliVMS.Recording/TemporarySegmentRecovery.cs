using System.Security.Cryptography;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>一輪暫存救援的結果。</summary>
/// <param name="Recovered">救回成可回放 final 的段數。</param>
/// <param name="Discarded">確定救不回來（檔案不是 MP4）而刪除暫存檔的段數。</param>
/// <param name="Skipped">本輪無法判定、留給下一輪的段數。</param>
/// <param name="Abandoned">
/// 磁碟還在、但正式檔與暫存檔都不在，因而把索引列收斂成 <c>missing</c> 的段數。
/// </param>
public readonly record struct TemporaryRecoveryReport(
    int Recovered,
    int Discarded,
    int Skipped,
    int Abandoned = 0)
{
    /// <summary>本輪是否真的改變了任何索引列。</summary>
    public bool AnythingChanged => Recovered > 0 || Discarded > 0 || Abandoned > 0;
}

/// <summary>
/// 把「中斷收尾」留下的 <c>seg-*.mp4.tmp</c> 救回索引，讓它變成可回放的 final 段。
///
/// <para>
/// 現場症狀：關閉程式之後資料夾裡躺著 <c>seg-125438353.mp4.tmp</c>，回放清單裡沒有那一段。
/// 索引記的是正式檔名（<c>seg-125438353.mp4</c>），磁碟上卻只有暫存檔，索引對帳因此把它判成
/// missing ——那一段影片就從時間軸上消失了，而 <see cref="RetentionService.PurgeStaleTmp"/> 會在
/// 十分鐘後把暫存檔本身也刪掉，變成永久損失。
/// </para>
/// <para>
/// 為什麼可以直接改名：錄影輸出是 fMP4（<c>frag_keyframe+empty_moov</c>），每個 fragment 都
/// 自我完整，寫到一半被砍掉的檔案前面幾秒仍然播得出來。判斷「這真的是 ffmpeg 的 MP4」只看
/// 第一個 box 是不是 <c>ftyp</c>（位移 4..7），不需要 ffprobe——救援發生在啟動路徑上，
/// 每個檔案都叫一次外部行程會讓開機變慢，而且 ffprobe 對半截檔本來就只會報錯。
/// </para>
/// <para>
/// 兩個刻意的保守選擇：
/// </para>
/// <list type="bullet">
/// <item>
/// <b>正在寫的檔案不動</b>：檔案時間在 <see cref="MinimumIdle"/> 內就跳過（錄影中的 ffmpeg
/// 會持續更新 mtime），Windows 上搬移也會因共用違規而失敗。無論如何，搬到一半的檔案不該被
/// 當成已完成。
/// </item>
/// <item>
/// <b>檔案不在時不標成 corrupt</b>：磁碟沒掛上（D: 被拔掉、網路磁碟掉線）與檔案真的不見了
/// 在磁碟上長得一模一樣；標成 corrupt 等於在磁碟回來之後報廢一段其實還在的錄影。寧可留著
/// 讓下一次回收再試（與 <see cref="SegmentIndexReconciler"/> 的「無法判定」同一個立場）。
/// 但「磁碟區還在、兩個檔案都不在」就不是無法判定：那種列會收斂成 missing，既不假裝救得回來，
/// 也讓它可以被磁碟匯入復活，而且批次不會被永遠掃不到的列吃光。
/// </item>
/// </list>
/// </summary>
public sealed class TemporarySegmentRecovery
{
    /// <summary>單輪最多處理的暫存段數。正常情況每頻道最多一段，這個上限只是防止異常累積。</summary>
    public const int DefaultBatchSize = 256;

    /// <summary>
    /// 檔案時間新於此值就視為「還在寫」，本輪跳過。
    ///
    /// <para>
    /// 30 秒足以涵蓋 ffmpeg 寫入的間隔（0.1 秒級），又短到讓「當掉後馬上重開」的檔案在下一輪
    /// 就能救回。真正的保護是 Windows 的共用違規——這裡只是不主動去碰。
    /// </para>
    /// </summary>
    public static readonly TimeSpan MinimumIdle = TimeSpan.FromSeconds(30);

    private readonly SegmentRepository _repo;
    private readonly Func<DateTime> _clock;
    private readonly Func<string, bool> _isVolumeReady;

    /// <param name="repo">區段索引。</param>
    /// <param name="clock">目前時間；測試注入固定時刻，避免依賴牆鐘。</param>
    /// <param name="isVolumeReady">
    /// 判定某路徑所在的磁碟區是否就緒；<c>false</c> 時「檔案不在」一律算無法判定（磁碟掉線）。
    /// 預設與 <see cref="SegmentIndexReconciler.IsVolumeReady"/> 同一套判定——兩邊各寫一份
    /// 「磁碟掉了還是檔案被刪了」的規則，遲早會有一邊把整顆磁碟的錄影標成 missing。
    /// </param>
    public TemporarySegmentRecovery(
        SegmentRepository repo,
        Func<DateTime>? clock = null,
        Func<string, bool>? isVolumeReady = null)
    {
        _repo = repo;
        _clock = clock ?? (static () => DateTime.UtcNow);
        _isVolumeReady = isVolumeReady ?? SegmentIndexReconciler.IsVolumeReady;
    }

    /// <summary>掃一輪尚未收尾的暫存段，能救的回索引、確定壞掉的標成 corrupt。</summary>
    public TemporaryRecoveryReport Run(int batchSize = DefaultBatchSize)
    {
        var now = _clock();
        var recovered = 0;
        var discarded = 0;
        var skipped = 0;
        var abandoned = 0;

        foreach (var row in Unfinished(batchSize))
        {
            if (string.IsNullOrWhiteSpace(row.FilePath))
            {
                _repo.MarkCorrupt(row.Id);
                discarded++;
                continue;
            }

            var finalPath = row.FilePath;
            var tmpPath = finalPath + ".tmp";
            var haveFinal = File.Exists(finalPath);
            var haveTmp = File.Exists(tmpPath);

            if (!haveFinal && !haveTmp)
            {
                // 磁碟不在（整顆拔掉、網路磁碟掉線）：無法判定，留著等下一輪（見類別說明）。
                // 這個檢查必須在「標成 missing」之前：一次掉線就會把整顆硬碟的段全部判死。
                if (!_isVolumeReady(finalPath))
                {
                    skipped++;
                    continue;
                }

                // 剛起的段在 BeginSegment 之後、ffmpeg 開檔之前是真的沒有檔案。此時判死會讓
                // 收尾時的 CompleteSegment（條件含 status='tmp'）一行都寫不進去——錄好的段
                // 明明在磁碟上，索引卻永遠停在 missing。用同一個「還在寫」的門檻擋住這個窗口。
                if (now - row.StartUtc < MinimumIdle)
                {
                    skipped++;
                    continue;
                }

                // corrupt 是「檔案在、但壞了」的歷史紀錄，沒有檔案時改寫它只會抹掉失敗原因。
                if (row.Status != SegmentStatus.Temporary)
                {
                    skipped++;
                    continue;
                }

                _repo.MarkMissingTemporary(row.Id);
                abandoned++;
                continue;
            }

            // 正式檔已經在（改名成功但索引沒寫進去，或兩份都留著）：以正式檔為準。
            var source = haveFinal ? finalPath : tmpPath;
            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(source);
            }
            catch (IOException)
            {
                skipped++;
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            if (now - lastWriteUtc < MinimumIdle)
            {
                skipped++;
                continue;
            }

            if (!Mp4Signature.LooksLikeMp4(source))
            {
                // 開頭就不是 MP4（空檔、半個 box header、被其他程式佔用後留下的垃圾）：
                // 這種檔案改什麼名字都播不出來，留著只會被保留清理再掃一次。
                _repo.MarkCorrupt(row.Id);
                TryDelete(tmpPath);
                discarded++;
                continue;
            }

            try
            {
                if (!haveFinal)
                {
                    File.Move(tmpPath, finalPath);
                }
                else
                {
                    TryDelete(tmpPath);
                }

                // 結束時間取檔案最後寫入時刻：那正是行程被砍掉、錄影實際停止的時間。
                var endUtc = File.GetLastWriteTimeUtc(finalPath);
                if (endUtc < row.StartUtc)
                {
                    // 檔案時間不可信（複製、時鐘調整）時退回起始時刻，長度記 0 而不是負數。
                    endUtc = row.StartUtc;
                }

                var size = new FileInfo(finalPath).Length;
                var sha256 = ComputeSha256(finalPath);
                _repo.CompleteSegment(
                    row.Id,
                    endUtc,
                    size,
                    Math.Max(0, (endUtc - row.StartUtc).TotalSeconds),
                    sha256);
                recovered++;
            }
            catch (IOException)
            {
                // 檔案仍被佔用（正在錄、防毒掃描）：下一輪再試。
                skipped++;
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return new TemporaryRecoveryReport(recovered, discarded, skipped, abandoned);
    }

    /// <summary>
    /// 本輪要看的未收尾列：先 <c>tmp</c>（正式的救援對象），再 <c>corrupt</c>（收尾失敗但檔案
    /// 可能還在磁碟上）。兩批各自有上限，否則一堆救不回來的 corrupt 列會把 tmp 擠出批次。
    /// </summary>
    private IEnumerable<SegmentRecord> Unfinished(int batchSize)
    {
        foreach (var row in _repo.ListTemporary(batchSize))
        {
            yield return row;
        }

        foreach (var row in _repo.ListCorruptNewest(batchSize))
        {
            yield return row;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 正在使用中：留給保留清理，不影響這一段的判定。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
