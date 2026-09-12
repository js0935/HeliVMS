using System.Diagnostics;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording;

/// <summary>
/// 錄影服務（§15：區段式錄影）。
/// 以 ffmpeg `-c copy` 直存 RTSP 主流（§21.2 #1：錄影不需解碼），
/// 每 segmentSeconds 產一個 mpegts 區段並回寫 SQLite 索引。
/// </summary>
public sealed class SegmentRecorder : IAsyncDisposable
{
    private readonly SegmentRepository _repo;
    private readonly string _ffmpeg;
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private long _activeSegmentId = -1;
    private string? _activePath;
    private Task? _loop;

    public SegmentRecorder(SegmentRepository repo, string? ffmpegPath = null)
    {
        _repo = repo;
        _ffmpeg = ffmpegPath ?? "ffmpeg";
    }

    public int ChannelId { get; private set; }

    public string? RtspUrl { get; private set; }

    public bool IsRecording => _loop is { IsCompleted: false };

    /// <summary>每段錄製秒數。</summary>
    public int SegmentSeconds { get; private set; } = 30;

    /// <summary>區段完成時觸發（已完成且索引已回寫）。</summary>
    public event EventHandler<SegmentRecord>? SegmentCompleted;

    /// <summary>開始錄影（背景分段迴圈）。</summary>
    public Task StartAsync(int channelId, string rtspUrl, string outputDirectory, int? segmentSeconds = null)
    {
        ChannelId = channelId;
        RtspUrl = rtspUrl;
        SegmentSeconds = segmentSeconds ?? 30;
        Directory.CreateDirectory(outputDirectory);
        _repo.EnsureChannel(channelId, $"ch-{channelId}", rtspUrl);

        _loop = Task.Run(() => RecordLoopAsync(outputDirectory, _cts.Token), _cts.Token);
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

    private async Task RecordLoopAsync(string outputDirectory, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RecordOneSegmentAsync(outputDirectory, token);
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

    private async Task RecordOneSegmentAsync(string outputDirectory, CancellationToken token)
    {
        var startUtc = DateTime.UtcNow;
        var filePath = Path.Combine(
            outputDirectory,
            $"ch{ChannelId:000}_{startUtc:yyyyMMdd_HHmmss}.ts");

        _activeSegmentId = _repo.BeginSegment(ChannelId, filePath, startUtc);
        _activePath = filePath;

        var exited = await RunSegmentAsync(filePath, token);

        if (!token.IsCancellationRequested && exited)
        {
            var size = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
            var endUtc = startUtc.AddSeconds(SegmentSeconds);
            _repo.CompleteSegment(_activeSegmentId, endUtc, size);
            SegmentCompleted?.Invoke(
                this,
                new SegmentRecord
                {
                    Id = _activeSegmentId,
                    ChannelId = ChannelId,
                    StartUtc = startUtc,
                    EndUtc = endUtc,
                    FilePath = filePath,
                    SizeBytes = size,
                    Format = "mpegts",
                    Status = SegmentStatus.Completed,
                });
        }
        else
        {
            _repo.MarkCorrupt(_activeSegmentId);
        }

        _activeSegmentId = -1;
        _activePath = null;
    }

    private async Task<bool> RunSegmentAsync(string filePath, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
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
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("mpegts");
        psi.ArgumentList.Add("-flush_packets");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(SegmentSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(filePath);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動 ffmpeg 錄影進程");
        _process = proc;

        try
        {
            await proc.WaitForExitAsync(token);
            _process = null;
            return true;
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}