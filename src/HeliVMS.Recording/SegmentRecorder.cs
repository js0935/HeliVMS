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
    private readonly SegmentRepository _repo;
    private readonly IProcessFactory _processFactory;
    private readonly Func<string, string> _audioEncoderArgsFor;
    private readonly Func<string>? _rootSelector;
    private readonly Func<int, string?>? _folderNameFor;
    private readonly FfmpegProcessRegistry _registry;
    private readonly Func<DateTime> _clock;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private IProcess? _testProcess;
    private long _activeSegmentId = -1;
    private string? _folderName;
    private Task? _loop;
    private bool _disposed;

    public SegmentRecorder(
        SegmentRepository repo,
        IProcessFactory? processFactory = null,
        Func<string, string>? audioEncoderArgsFor = null,
        Func<string>? rootSelector = null,
        FfmpegProcessRegistry? registry = null,
        Func<int, string?>? folderNameFor = null,
        Func<DateTime>? clock = null)
    {
        _repo = repo;
        _processFactory = processFactory ?? new DefaultProcessFactory();
        _audioEncoderArgsFor = audioEncoderArgsFor ?? DefaultAudioEncoderArgsFor;
        _rootSelector = rootSelector;
        _registry = registry ?? FfmpegProcessRegistry.Shared;
        _folderNameFor = folderNameFor;
        _clock = clock ?? (static () => DateTime.UtcNow);
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

    /// <summary>停止錄影：終止目前區段並標記異常（未收尾）。</summary>
    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        _cts.Cancel();
        KillProcess();

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // 預期
            }

            _loop = null;
        }
    }

    private async Task RecordLoopAsync(string recordingsRoot, CancellationToken token)
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

                continue;
            }

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
            if (!token.IsCancellationRequested && ok)
            {
                // 結束時間取真實收尾時刻，而不是 `start + 設定分段秒數`：攝影機 RTSP 突然中斷時，
                // ffmpeg 會提早以退出碼 0 結束，檔案只有幾秒，卻被記成完整一段——時間軸與 HLS
                // 都宣稱有整整 60 秒影片，播放器一到檔案真正結尾就卡住。量到的牆鐘時間會貼近
                // 實際長度（含極短的連線暖機，誤差在秒的下方）。結束時間要在雜湊之前取，
                // 否則大檔的 SHA-256 讀取會被算進影片長度。
                var endUtc = _clock();
                File.Move(tmpPath, finalPath);
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
            TryDeleteTmp(tmpPath);
            _repo.MarkCorrupt(_activeSegmentId);
            return false;
        }
        finally
        {
            _activeSegmentId = -1;
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
            await proc.WaitForExitAsync(token);
            _lastExitCode = proc.ExitCode;

            // ffmpeg 的 stderr 會以 "Input #0, rtsp, from 'rtsp://user:pass@host/...'" 回顯輸入網址；
            // 整段不是合法 URI，必須用 RedactText 逐段移除 userinfo，Redact 對此無效。
            var stderr = RtspUri.RedactText(await stderrTask);
            lock (LastUtfError)
            {
                LastUtfError.Append(stderr);
            }

            if (proc.ExitCode != 0)
            {
                job.Fail(stderr);
            }

            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            job.Fail("錄影已取消");
            KillProcess();
            return false;
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