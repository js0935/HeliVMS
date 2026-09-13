using System.Diagnostics;
using System.Security.Cryptography;
using HeliVMS.Media;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 錄影服務（§3.2 錄影管道：fMP4、`-c copy`、AAC 轉碼規則）。
/// ffmpeg 直拉 RTSP 主流寫烘時暫存檔，區段收尾後改成正式檔並計算 SHA-256。
/// </summary>
public sealed class SegmentRecorder : IAsyncDisposable
{
    public const int DefaultSegmentSeconds = 600;
    private readonly SegmentRepository _repo;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private long _activeSegmentId = -1;
    private Task? _loop;

    public SegmentRecorder(SegmentRepository repo)
    {
        _repo = repo;
    }

    public int ChannelId { get; private set; }

    public string Stream { get; private set; } = "main";

    public string? RtspUrl { get; private set; }

    public bool IsRecording => _loop is { IsCompleted: false };

    /// <summary>每段錄製秒數。</summary>
    public int SegmentSeconds { get; private set; } = DefaultSegmentSeconds;

    /// <summary>區段完成時觸發（已改名為正式檔且索引已回寫）。</summary>
    public event EventHandler<SegmentRecord>? SegmentCompleted;

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

        _loop = Task.Run(() => RecordLoopAsync(recordingsRoot, _cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>停止錄影：終止目前區段並標記異常（未收尾）。</summary>
    public async Task StopAsync()
    {
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
            try
            {
                audioEncoderArgs ??= ProbeAudioEncoderArgs();
                await RecordOneSegmentAsync(recordingsRoot, audioEncoderArgs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                if (_activeSegmentId >= 0)
                {
                    _repo.MarkCorrupt(_activeSegmentId);
                }

                try
                {
                    await Task.Delay(5_000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RecordOneSegmentAsync(string recordingsRoot, string audioEncoderArgs, CancellationToken token)
    {
        var startUtc = DateTime.UtcNow;
        var dir = Path.Combine(recordingsRoot, $"ch{ChannelId:000}", startUtc.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);

        var finalPath = Path.Combine(dir, $"seg-{startUtc:HHmmss}.mp4");
        var tmpPath = finalPath + ".tmp";

        _activeSegmentId = _repo.BeginSegment(ChannelId, Stream, finalPath, startUtc);

        try
        {
            var ok = await RunSegmentAsync(tmpPath, audioEncoderArgs, token);
            if (!token.IsCancellationRequested && ok)
            {
                File.Move(tmpPath, finalPath);
                var size = new FileInfo(finalPath).Length;
                var sha256 = ComputeSha256(finalPath);
                var endUtc = startUtc.AddSeconds(SegmentSeconds);
                _repo.CompleteSegment(_activeSegmentId, endUtc, size, SegmentSeconds, sha256);

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
                        DurationSec = SegmentSeconds,
                        Format = "mp4",
                        Status = SegmentStatus.Final,
                        Sha256 = sha256,
                    });
            }
            else
            {
                TryDeleteTmp(tmpPath);
                _repo.MarkCorrupt(_activeSegmentId);
            }
        }
        catch
        {
            TryDeleteTmp(tmpPath);
            _repo.MarkCorrupt(_activeSegmentId);
        }
        finally
        {
            _activeSegmentId = -1;
        }
    }

    private string ProbeAudioEncoderArgs()
    {
        var info = StreamProbe.Probe(RtspUrl!);
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
            FileName = "ffmpeg",
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

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg 錄影進程");
        _process = proc;

        try
        {
            await proc.WaitForExitAsync(token);
            _process = null;
            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            KillProcess();
            return false;
        }
        finally
        {
            _process = null;
        }
    }

    private void KillProcess()
    {
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
        await StopAsync();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}