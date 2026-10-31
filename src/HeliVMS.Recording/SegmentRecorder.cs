using System.Diagnostics;
using System.Security.Cryptography;
using HeliVMS.Media;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 錄影服務（§3.2 錄影管道：fMP4、`-c copy`、AAC 轉碼規則）。
/// ffmpeg 直拉 RTSP 主流寫烘時暫存檔，區段收尾後改成正式檔並計算 SHA-256。
/// </summary>
public sealed class SegmentRecorder : ISegmentRecorder
{
    /// <summary>
    /// 未指定分段秒數時的後備值。正式路徑（排程與監看牆錄影）一律傳入
    /// <see cref="RecordingSegmentPolicy.Read"/> 的設定值，此常數僅供直接建構
    /// <see cref="SegmentRecorder"/> 時使用；與政策預設共用同一個數字，避免兩處分歧。
    /// </summary>
    public const int DefaultSegmentSeconds = RecordingSegmentPolicy.DefaultSeconds;

    /// <summary>
    /// 送出收尾請求後等 ffmpeg 自己結束的上限。
    ///
    /// <para>
    /// ffmpeg 收到 <c>q</c> 之後要把當前 fragment 與 moov 寫完才會結束，正常是幾百毫秒；
    /// 8 秒是「攝影機卡住、磁碟很慢」的兜底。不能無限等，也不能太短：太短就退回硬砍，
    /// 得到的正是要修掉的 <c>seg-*.mp4.tmp</c>。
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultGracefulStopTimeout = TimeSpan.FromSeconds(8);

    private readonly SegmentRepository _repo;
    private readonly IProcessFactory _processFactory;
    private readonly Func<string, string> _audioEncoderArgsFor;
    private readonly Func<string>? _rootSelector;
    private readonly Func<int, string?>? _folderNameFor;
    private readonly FfmpegProcessRegistry _registry;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _gracefulStopTimeout;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private IProcess? _testProcess;
    private long _activeSegmentId = -1;
    private string? _folderName;
    private Task? _loop;
    private bool _disposed;

    /// <summary>目前這一段開始錄的時間；null 表示沒有在錄（含正介於兩段之間）。</summary>
    private DateTime? _activeSegmentStartUtc;

    /// <summary>
    /// 正在等目前這一段收尾的呼叫者（回放視窗）。用集合而不是單一欄位：
    /// 兩個回放視窗同時開在同一個頻道上時，兩邊都該在檔案出現後立刻看到它。
    /// </summary>
    private readonly List<TaskCompletionSource<bool>> _cutWaiters = [];

    private readonly object _cutGate = new();

    /// <summary>
    /// 短於此長度的段不值得為了一次回放而截斷：剛起的段本來就沒有內容，
    /// 截斷只會在索引裡留下一段長度趨近 0 的錄影（時間軸上幾乎點不到，
    /// 卻多一列索引、多一個檔案），而操作員連按兩次回放時也根本沒有新的畫面可看。
    /// </summary>
    public static readonly TimeSpan MinCutAge = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 同一台錄影機兩次收檔之間的最小間隔。
    /// </summary>
    /// <remarks>
    /// 收檔＝提前換段＝重新連一次 RTSP 並等第一個 IDR，中間會掉約 0.5～2 秒畫面。
    /// 沒有這道冷卻，操作員連按回放（或兩個回放視窗輪流開同一個頻道）就會讓錄影
    /// 一直在重連：多出一堆長度十幾秒的碎片檔，而錄影本身反而殘破。
    /// 30 秒的意義是「一次回放最多換掉半個正常分段」，最壞情況只多約 10% 檔案。
    /// </remarks>
    public static readonly TimeSpan CutCooldown = TimeSpan.FromSeconds(30);

    /// <summary>上一次收檔的時間；<see cref="CutCooldown"/> 用它避免連續截斷。</summary>
    private DateTime? _lastCutUtc;

    public SegmentRecorder(
        SegmentRepository repo,
        IProcessFactory? processFactory = null,
        Func<string, string>? audioEncoderArgsFor = null,
        Func<string>? rootSelector = null,
        FfmpegProcessRegistry? registry = null,
        Func<int, string?>? folderNameFor = null,
        Func<DateTime>? clock = null,
        TimeSpan? gracefulStopTimeout = null)
    {
        _repo = repo;
        _processFactory = processFactory ?? new DefaultProcessFactory();
        _audioEncoderArgsFor = audioEncoderArgsFor ?? DefaultAudioEncoderArgsFor;
        _rootSelector = rootSelector;
        _registry = registry ?? FfmpegProcessRegistry.Shared;
        _folderNameFor = folderNameFor;
        _clock = clock ?? (static () => DateTime.UtcNow);
        _gracefulStopTimeout = gracefulStopTimeout ?? DefaultGracefulStopTimeout;
    }

    public int ChannelId { get; private set; }

    public string Stream { get; private set; } = "main";

    public string? RtspUrl { get; private set; }

    public bool IsRecording => _loop is { IsCompleted: false };

    /// <summary>每段錄製秒數。</summary>
    public int SegmentSeconds { get; private set; } = DefaultSegmentSeconds;

    /// <summary>區段完成時觸發（已改名為正式檔且索引已回寫）。</summary>
    public event EventHandler<SegmentRecord>? SegmentCompleted;

    /// <summary>
    /// 連續失敗達 <see cref="FailureReportThreshold"/> 次時觸發一次。
    ///
    /// 存在的理由是「離線的攝影機看起來跟正常的一模一樣」：ffmpeg 連不上時
    /// <see cref="IsRecording"/> 仍然是 true（背景迴圈在跑，只是每段都失敗），
    /// 稽核裡也已經寫過 recording.start。使用者從稽核看是「有在錄」，
    /// 磁碟上卻沒有檔案，沒有任何地方會說話。這種失效在無人值守的現場
    /// 可以持續幾天沒人發現，所以它必須自己開口。
    /// </summary>
    public event EventHandler<RecordingStallInfo>? RecordingStalled;

    /// <summary>
    /// 曾經停滯、如今又成功錄回檔（參數為頻道 ID）。
    /// </summary>
    /// <remarks>
    /// 沒有這條訊號，UI 上的「錄影異常」只能靠逾時猜測要不要清掉。但不能用逾時判斷：
    /// 顯示串流（<c>ChannelSession.Client</c>）與錄影串流（<c>SegmentRecorder</c>）是兩條
    /// 獨立連線，停滯時顯示畫面照常有幀、<see cref="IsRecording"/> 也仍是 true，
    /// 任何「畫面有在動就算好了」的判斷都會立刻把警示清掉——正好在最該警示的時候。
    /// 唯一可信的恢復證據是這裡真的寫出了一段。
    /// </remarks>
    public event EventHandler<int>? RecordingResumed;

    /// <summary>連續失敗幾次後發出一次停滯通知。</summary>
    public const int FailureReportThreshold = 3;

    /// <summary>退避上限：離線的攝影機不可能靠更频繁重試就连上线。</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>首次失敗後的退避基準。</summary>
    public static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 連續失敗的退避時間，指數成長到 <see cref="MaxRetryDelay"/> 為止。
    ///
    /// 固定 5 秒重試看起來很勤勞，實際後果是每次失敗都往 segments 表寫一筆
    /// corrupt：一台拔掉網路線的攝影機一天 17,280 筆，16 台就是 27 萬筆，
    /// 而 segments 是回放時間軸與用量統計都會掃的表。指數退避把它降到
    /// 每天每頻道 288 筆以內，重連的反應時間最多只犧牲到 5 分鐘。
    /// </summary>
    public static TimeSpan RetryDelayFor(int consecutiveFailures)
        => FfmpegBackoff.DelayFor(consecutiveFailures, BaseRetryDelay, MaxRetryDelay);

    private int _consecutiveFailures;
    private bool _stallReported;

    /// <summary>開始錄影（背景分段迴圈）。</summary>
    public Task StartAsync(
        int channelId,
        string rtspUrl,
        string recordingsRoot,
        string stream = "main",
        int? segmentSeconds = null)
    {
        ChannelId = channelId;
        RtspUrl = rtspUrl;
        Stream = stream;
        SegmentSeconds = segmentSeconds ?? DefaultSegmentSeconds;

        // 資料夾名稱在開錄時決定一次並固定：頻道名稱之後若被改動，這次錄影的所有區段仍留在同一目錄，
        // 不會同一段錄影被拆到兩個資料夾。
        _folderName = _folderNameFor?.Invoke(channelId);

        _loop = Task.Run(() => RecordLoopAsync(recordingsRoot, _cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止錄影：請 ffmpeg 正常收尾（送出 <c>q</c>）並等它把當前區段寫完、改成正式檔。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 這裡以前是「取消 → 立刻 <c>Kill()</c>」。被硬砍的 ffmpeg 留下
    /// <c>seg-HHmmssfff.mp4.tmp</c>，而索引寫的是正式檔名，於是索引對帳把它判成 missing：
    /// 每次關閉程式都吃掉最後一段錄影，畫面完全看不出來（.tmp 就靜靜躺在資料夾裡）。
    /// </para>
    /// <para>
    /// 收尾仍然有上限（<see cref="_gracefulStopTimeout"/>）：關機不能無限等一台卡住的攝影機，
    /// 等不到才硬砍。改成寫 <c>q</c> 之後，正常情況這一段會被完整收檔並寫進索引。
    /// </para>
    /// </remarks>
    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        _cts.Cancel();

        if (_loop is not null)
        {
            try
            {
                // 多給一點餘裕：區段收尾＝ffmpeg 寫檔 ＋ SHA-256 ＋ 索引回寫。
                await _loop.WaitAsync(_gracefulStopTimeout + TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                LastFailure = "錄影收尾逾時，已強制結束 ffmpeg。";
                KillProcess();
                try
                {
                    await _loop;
                }
                catch (OperationCanceledException)
                {
                    // 預期
                }
                catch (Exception)
                {
                    // 收尾已經失敗，不該讓關閉流程再爆一次
                }
            }
            catch (OperationCanceledException)
            {
                // 預期
            }

            _loop = null;
        }
    }

    /// <summary>
    /// 要求目前在錄的這一段立刻收尾，並等它成為可回放的正式檔。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 收檔＝對 ffmpeg 送出 <c>q</c>：它會把當前 fragment 寫完、關閉檔案再以退出碼 0 結束，
    /// 也就是與「<c>-t</c> 到期」完全相同的收尾路徑。背景迴圈接著就開下一段，
    /// 錄影沒有停止，只是提前換段。
    /// </para>
    /// <para>
    /// 不切剛起的段（<see cref="MinCutAge"/>）：那會在索引留下一段長度趨近 0 的錄影，
    /// 而下一次按回放本來也沒有新內容。
    /// </para>
    /// <para>
    /// 同一台 30 秒內只切一次（<see cref="CutCooldown"/>）：收檔是一次換段，
    /// 每個來回會掉一小段畫面，連按回放不該讓錄影一直在重連。
    /// </para>
    /// <para>
    /// 逾時一定要回 false 而不是無限等：呼叫端是回放視窗的載入流程，
    /// 一台卡住的攝影機不能讓回放開不起來。
    /// </para>
    /// </remarks>
    public async Task<bool> CutAsync(TimeSpan timeout)
    {
        if (_disposed || !IsRecording)
        {
            return false;
        }

        if (_activeSegmentStartUtc is not { } started || _clock() - started < MinCutAge)
        {
            return false;
        }

        if (_lastCutUtc is { } lastCut && _clock() - lastCut < CutCooldown)
        {
            return false;
        }

        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cutGate)
        {
            _cutWaiters.Add(waiter);
        }

        // 介於兩段之間（ffmpeg 已結束、下一台還沒起來）時沒有任何東西可切：
        // 那一段的收尾已經在跑，等它反而會多等一個逾時。
        if (!TryRequestQuitForCut())
        {
            lock (_cutGate)
            {
                _cutWaiters.Remove(waiter);
            }

            return false;
        }

        try
        {
            // 冷卻從「真的送出收尾請求」起算：介於兩段之間或送不出去時，
            // 不該害下一次回放被冷卻擋掉。
            _lastCutUtc = _clock();
            return await waiter.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            lock (_cutGate)
            {
                _cutWaiters.Remove(waiter);
            }
        }
    }

    /// <summary>對目前這一段的 ffmpeg 送出收尾請求；沒有行程可送時回 false。</summary>
    private bool TryRequestQuitForCut()
    {
        var proc = _testProcess;
        if (proc is null)
        {
            return false;
        }

        try
        {
            if (proc.HasExited)
            {
                return false;
            }

            proc.RequestQuit();
            return true;
        }
        catch (InvalidOperationException)
        {
            // 行程物件剛被釋放（ObjectDisposedException 是它的子類）：這一段正在自己收尾。
            return false;
        }
    }

    /// <summary>這一段有結果了：放行正在等它收尾的回放視窗。</summary>
    private void CompleteCutWaiters(bool completed)
    {
        TaskCompletionSource<bool>[] waiters;
        lock (_cutGate)
        {
            waiters = [.. _cutWaiters];
            _cutWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(completed);
        }
    }

    private async Task RecordLoopAsync(string recordingsRoot, CancellationToken token)
    {
        try
        {
            await RecordLoopCoreAsync(recordingsRoot, token);
        }
        finally
        {
            // 迴圈結束（停止錄影、例外）時不能讓等收檔的人等到逾時。
            CompleteCutWaiters(completed: false);
        }
    }

    private async Task RecordLoopCoreAsync(string recordingsRoot, CancellationToken token)
    {
        string? audioEncoderArgs = null;
        while (!token.IsCancellationRequested)
        {
            bool succeeded;
            try
            {
                audioEncoderArgs ??= _audioEncoderArgsFor(RtspUrl!);

                // 每個區段都重新挑碟（多磁碟溢出）：主要碟滿了自然換下一顆；沒有選擇器時
                // 沿用啟動時給的單一根目錄。選擇器回空字串視為「挑不到」，同樣退回原根目錄。
                var root = _rootSelector?.Invoke();
                if (string.IsNullOrWhiteSpace(root))
                {
                    root = recordingsRoot;
                }

                succeeded = await RecordOneSegmentAsync(root, audioEncoderArgs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 連目錄都開不出來、索引寫不進去這種層級的失敗：不會有區段記錄可標記。
                LastFailure = ex.ToString();
                succeeded = false;
            }

            if (succeeded)
            {
                // 重連成功後歸零，否則一次早年的斷線會讓之後每次失敗都重新「通知」一次。
                _consecutiveFailures = 0;
                if (_stallReported)
                {
                    _stallReported = false;
                    RecordingResumed?.Invoke(this, ChannelId);
                }

                // 這一段已經成為 final：按回放而要求收檔的視窗可以載入時間軸了。
                CompleteCutWaiters(completed: true);
                continue;
            }

            // 這一段沒錄成，等它的人也不會看到新檔案——但不能讓他們等到逾時。
            CompleteCutWaiters(completed: false);

            _consecutiveFailures++;
            if (!_stallReported && _consecutiveFailures >= FailureReportThreshold)
            {
                _stallReported = true;
                RecordingStalled?.Invoke(this, new RecordingStallInfo(
                    ChannelId,
                    _consecutiveFailures,
                    LastFailure ?? string.Empty,
                    LastFfError));
            }

            try
            {
                await Task.Delay(RetryDelayFor(_consecutiveFailures), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>本段是否真的錄成正式檔。</summary>
    private async Task<bool> RecordOneSegmentAsync(string recordingsRoot, string audioEncoderArgs, CancellationToken token)
    {
        var startUtc = _clock();
        _activeSegmentStartUtc = startUtc;
        // 以頻道名稱命名資料夾（使用者看得懂）；名稱從缺或未提供時退回舊制 ch{id}。
        var folder = string.IsNullOrWhiteSpace(_folderName)
            ? RecordingFolderNaming.LegacyName(ChannelId)
            : _folderName!;
        var dir = Path.Combine(recordingsRoot, folder, startUtc.ToString("yyyyMMdd"), startUtc.ToString("HH"));
        Directory.CreateDirectory(dir);

        // 檔名帶毫秒：秒級（`HHmmss`）在「攝影機 RTSP 串流突然 EOF、ffmpeg 迅速以 0 結束」
        // 這種情況下會出包——同一秒內就開始下一段，`File.Move` 撞到同名檔會擲 IOException，
        // 被 catch 當成損壞段刪掉，該段錄影直接消失，log 只有一筆 corrupt。逐段錄影是循序阻塞的，
        // 毫秒級足以保證唯一（跨串流／跨頻道另有資料夾與單一錄影器規則擋住）。
        var finalPath = Path.Combine(dir, $"seg-{startUtc:HHmmssfff}.mp4");
        var tmpPath = finalPath + ".tmp";

        _activeSegmentId = _repo.BeginSegment(ChannelId, Stream, finalPath, startUtc);

        try
        {
            var ok = await RunSegmentAsync(tmpPath, audioEncoderArgs, token);
            if (ok)
            {
                // 先改名再看時間：改名是這段錄影「已經成為正式檔」的分界點。放在取時間之前，
                // 是為了讓「改名成功、後面任何一步失敗」都落在同一條補救路徑上（見 catch）。
                File.Move(tmpPath, finalPath);

                // 結束時間取真實收尾時刻，而不是 `start + 設定分段秒數`：攝影機 RTSP 突然中斷時，
                // ffmpeg 會提早以退出碼 0 結束，檔案只有幾秒，卻被記成完整一段——時間軸與 HLS
                // 都宣稱有整整 60 秒影片，播放器一到檔案真正結尾就卡住。量到的牆鐘時間會貼近
                // 實際長度（含極短的連線暖機，誤差在秒的下方）。結束時間要在雜湊之前取，
                // 否則大檔的 SHA-256 讀取會被算進影片長度。
                //
                // 條件刻意不再加「且未被取消」：取消時是「請 ffmpeg 收尾」，不是「這段不要了」。
                // 被取消就丟掉的話，關程式前錄的那一段會永遠離開索引（正是要修的症狀）。
                var endUtc = _clock();
                var size = new FileInfo(finalPath).Length;
                var sha256 = ComputeSha256(finalPath);
                var durationSec = Math.Max(0, (endUtc - startUtc).TotalSeconds);
                _repo.CompleteSegment(_activeSegmentId, endUtc, size, durationSec, sha256);

                SegmentCompleted?.Invoke(
                    this,
                    new SegmentRecord
                    {
                        Id = _activeSegmentId,
                        ChannelId = ChannelId,
                        Stream = Stream,
                        StartUtc = startUtc,
                        EndUtc = endUtc,
                        FilePath = finalPath,
                        SizeBytes = size,
                        DurationSec = durationSec,
                        Format = "mp4",
                        Status = SegmentStatus.Final,
                        Sha256 = sha256,
                    });
                return true;
            }

            // ffmpeg 收工但沒有可用輸出（連不上、無資料流、容器不合）：不能算成功，
            // 否則上層會以為這台攝影機一切正常。
            TryDeleteTmp(tmpPath);
            _repo.MarkCorrupt(_activeSegmentId);
            return false;
        }
        catch (Exception ex)
        {
            LastFailure = ex.ToString();

            // 改名成功、但後面（取時間、算雜湊、寫索引）失敗：磁碟上是一段完整的錄影，
            // 不是壞檔。以前這裡一律 TryDeleteTmp + MarkCorrupt，而 .tmp 早就被搬走了，
            // 結果是「檔案好好地在資料夾裡、索引記成 corrupt」，回放只讀 final 所以看不到，
            // 磁碟匯入又會把它當成「已索引」跳過——那段影片就永遠消失了。
            if (TryCompleteMovedSegment(_repo, _activeSegmentId, finalPath, tmpPath, startUtc))
            {
                return true;
            }

            TryDeleteTmp(tmpPath);
            _repo.MarkCorrupt(_activeSegmentId);
            return false;
        }
        finally
        {
            _activeSegmentId = -1;
            _activeSegmentStartUtc = null;
        }
    }

    /// <summary>
    /// 改名成功、後續步驟失敗時，用磁碟上的事實（大小、最後寫入時間、SHA-256）補完這一段。
    /// </summary>
    /// <param name="repo">區段索引。</param>
    /// <param name="segmentId">要收尾的列。</param>
    /// <param name="finalPath">正式檔路徑。</param>
    /// <param name="tmpPath">暫存檔路徑（用來確認改名真的發生過）。</param>
    /// <param name="startUtc">段開始時間；檔案時間比它舊（時鐘被調整）時退回它，長度記 0 而不是負值。</param>
    /// <returns>是否已把這段收尾成 final。</returns>
    /// <remarks>
    /// 「暫存檔還在」就一律不動：那只代表 <see cref="File.Move(string, string)"/> 根本沒成功（最常見的原因是
    /// 目的地撞名），而 <paramref name="finalPath"/> 上那個檔案是<b>別人</b>的段。把別人的錄影
    /// 記到這個頻道名下就是證據錯置，比看不到更糟。
    /// 抽成 static 是為了能直接測：這個路徑只在「收尾途中出錯」時才會走到，用真實行程模擬不出來。
    /// </remarks>
    internal static bool TryCompleteMovedSegment(
        SegmentRepository repo,
        long segmentId,
        string finalPath,
        string tmpPath,
        DateTime startUtc)
    {
        if (segmentId <= 0 || File.Exists(tmpPath) || !File.Exists(finalPath))
        {
            return false;
        }

        try
        {
            var size = new FileInfo(finalPath).Length;
            if (size <= 0)
            {
                return false;
            }

            var endUtc = File.GetLastWriteTimeUtc(finalPath);
            if (endUtc < startUtc)
            {
                endUtc = startUtc;
            }

            var durationSec = Math.Max(0, (endUtc - startUtc).TotalSeconds);
            var sha256 = ComputeSha256(finalPath);
            repo.CompleteSegment(segmentId, endUtc, size, durationSec, sha256);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 檔案仍被佔用或索引寫不進去：留給啟動／每小時的暫存救援（TemporarySegmentRecovery），
            // 它會看到「正式檔已存在」而把這段補完。
            return false;
        }
    }

    /// <summary>
    /// 預設音訊參數決策：以 ffprobe 探測一次，依結果決定 copy／aac 轉碼／不錄音。
    /// 抽成可注入的委派，是為了讓 <see cref="SegmentRecorder"/> 的錄影收尾行為能在
    /// 不連攝影機、不跑 ffprobe 的情況下被測（見 `SegmentRecorderTests`）。
    /// </summary>
    private static string DefaultAudioEncoderArgsFor(string rtspUrl)
    {
        var info = StreamProbe.Probe(rtspUrl);
        return info.AudioCodec switch
        {
            null => string.Empty,
            "aac" => "-c:a copy",
            _ => "-c:a aac -b:a 128k -ac 1 -ar 44100",
        };
    }

    /// <summary>執行一段錄影，回傳是否正常收尾。</summary>
    private async Task<bool> RunSegmentAsync(string tmpPath, string audioEncoderArgs, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.Ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            // 收尾要靠 stdin 的 'q'：取消時不是砍掉行程，而是請它把這段寫完。
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-rtsp_transport");
        psi.ArgumentList.Add("tcp");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(RtspUrl!);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");

        if (audioEncoderArgs.Length > 0)
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:a:0?");
        }

        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("copy");
        if (audioEncoderArgs.Length > 0)
        {
            foreach (var arg in audioEncoderArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                psi.ArgumentList.Add(arg);
            }
        }

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("frag_keyframe+empty_moov+default_base_moof+faststart");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(SegmentSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(tmpPath);

        var proc = _processFactory.Start(psi);
        _process = proc as Process;
        _testProcess = proc;
        LastUtfError.Clear();
        _lastExitCode = null;

        // 登錄到全域行程表：錄影 ffmpeg 是每頻道一個的常駐子行程，健康視窗要能一眼看到總量。
        // 處置即代表此行程結束；非零退出另計為失敗。
        using var job = _registry.Register(FfmpegJobKind.Record, $"ch{ChannelId:000}/{Stream}");

        // ffmpeg 的 stderr 必須「邊跑邊讀」：管線緩衝區滿了會讓 ffmpeg 卡在寫入而不結束，
        // WaitForExitAsync 就永遠等不到——畫面看起來有在錄，磁碟卻一個檔都沒有，log 也乾淨。
        // 因此在等待結束之前就先啟動讀取，結束後再取結果。（-loglevel error 讓輸出通常很小，
        // 但一台反覆連不上的攝影機足以把緩衝區塞滿。）
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            var exitTask = proc.WaitForExitAsync(CancellationToken.None);
            var killed = false;

            try
            {
                await exitTask.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                // 取消＝「請它收尾」，不是「砍掉它」。硬砍的後果是 seg-*.mp4.tmp：索引寫的是
                // 正式檔名，磁碟上卻只有暫存檔，對帳因此判成 missing——那段錄影永遠播不出來。
                proc.RequestQuit();

                try
                {
                    await exitTask.WaitAsync(_gracefulStopTimeout);
                }
                catch (TimeoutException)
                {
                    job.Fail("錄影收尾逾時，強制結束 ffmpeg");
                    KillProcess();
                    killed = true;
                    try
                    {
                        await exitTask;
                    }
                    catch (Exception)
                    {
                        // 被砍掉的行程不一定會「正常」結束；收尾失敗已經記在 registry。
                    }
                }
            }

            _lastExitCode = proc.ExitCode;

            // ffmpeg 的 stderr 會以 "Input #0, rtsp, from 'rtsp://user:pass@host/...'" 回顯輸入網址；
            // 整段不是合法 URI，必須用 RedactText 逐段移除 userinfo，Redact 對此無效。
            var stderr = RtspUri.RedactText(await stderrTask);
            lock (LastUtfError)
            {
                LastUtfError.Append(stderr);
            }

            if (killed)
            {
                // 被硬砍的檔案尾端可能缺一個 fragment；不能當成正常收尾改名成正式檔
                // （時間軸會多出一段「看得到、播到一半卡住」的錄影）。
                return false;
            }

            if (proc.ExitCode != 0)
            {
                job.Fail(stderr);
            }

            return proc.ExitCode == 0;
        }
        finally
        {
            _process = null;
            _testProcess = null;
            // 明確釋放行程與管線控制代碼；否則每個區段、每次重試都漏一組 handle 直到 GC 回收。
            await proc.DisposeAsync();
        }
    }

    /// <summary>最近一次 ffmpeg 之 stderr（診斷用）。</summary>
    public string LastFfError
    {
        get
        {
            lock (LastUtfError)
            {
                return LastUtfError.ToString().Trim();
            }
        }
    }

    /// <summary>最近一次錄影失敗之例外描述（診斷用）。</summary>
    public string? LastFailure { get; private set; }

    /// <summary>最近一次 ffmpeg 退出碼（診斷用）。</summary>
    public int? LastExitCode
    {
        get => _lastExitCode;
        set => _lastExitCode = value;
    }

    private int? _lastExitCode;
    private readonly System.Text.StringBuilder LastUtfError = new();

    private void KillProcess()
    {
        var tp = _testProcess;
        if (tp is not null)
        {
            try
            {
                if (!tp.HasExited)
                {
                    tp.Kill();
                }
            }
            catch
            {
            }

            return;
        }

        var p = _process;
        if (p is null)
        {
            return;
        }

        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 已結束
        }
    }

    private static void TryDeleteTmp(string path)
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
            // 保留暫存檔供啟動掃描
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        _disposed = true;
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}