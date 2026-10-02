using System.Diagnostics;
using System.Net;
using System.Text;
using HeliVMS.Shared;

namespace HeliVMS.Rtc;

/// <summary>publisher 的可觀察狀態。</summary>
public enum PublisherState
{
    /// <summary>尚未啟動。</summary>
    Stopped,

    /// <summary>ffmpeg 已啟動，還沒收到第一個 RTP 封包。</summary>
    Starting,

    /// <summary>已收到封包，畫面應該出來了。</summary>
    Streaming,

    /// <summary>ffmpeg 已結束或被中止。</summary>
    Faulted,
}

/// <summary>
/// 單一通道的 ffmpeg publisher：把攝影機的 RTSP 轉成 H.264 RTP 送到 loopback。
/// <para>
/// 每通道一個進程，參數由 <see cref="LiveEncodeOptions"/> 決定。
/// 這是整個 M244 唯一的編解碼成本來源——SFU 本身不解碼，所以<b>CPU 只隨通道數成長</b>。
/// </para>
/// </summary>
public sealed class LivePublisher : IAsyncDisposable
{
    private readonly WhepOptions _options;
    private readonly LiveEncodeOptions _encode;
    private readonly RedactingErrorBuffer _stderr = new();
    private Process? _process;
    private int _exitCode;

    public LivePublisher(WhepOptions options, LiveEncodeOptions encode)
    {
        _options = options;
        _encode = encode;
    }

    /// <summary>目前狀態。</summary>
    public PublisherState State { get; private set; } = PublisherState.Stopped;

    /// <summary>ffmpeg 的結束碼；仍在執行時為 <c>null</c>。</summary>
    public int? ExitCode => _exitCode == int.MinValue ? null : _exitCode;

    /// <summary>最近一次 ffmpeg 輸出；已做憑證遮蔽。</summary>
    public string LastError => _stderr.Text;

    /// <summary>
    /// 啟動 publisher。
    /// </summary>
    /// <remarks>
    /// <c>rtpTarget</c> 一律由呼叫端給出 loopback 位址；這裡不推導外部位址，
    /// 以免日後有人把未加密串流推上網路。
    /// </remarks>
    public Process Start(string rtspUrl, IPEndPoint rtpTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rtspUrl);
        ArgumentNullException.ThrowIfNull(rtpTarget);

        if (_process is not null)
        {
            throw new InvalidOperationException("publisher 已在執行中");
        }

        var target = $"rtp://{rtpTarget.Address}:{rtpTarget.Port}";

        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in _encode.BuildArguments(rtspUrl, target))
        {
            psi.ArgumentList.Add(arg);
        }

        var process = Process.Start(psi)
            ?? throw new PublisherStartException($"無法啟動 {_options.FfmpegPath}：找不到執行檔。");

        _process = process;
        _exitCode = int.MinValue;
        State = PublisherState.Starting;
        _stderr.Clear();

        // ffmpeg 的 stderr 會回顯 "Input #0, rtsp, from 'rtsp://user:pass@host/...'"。
        // 逐行讀並即時遮蔽，絕不讓明文帳密進到記錄或 API 回應裡。
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    // 逐行遮蔽：ffmpeg 的 stderr 會回顯 "Input #0, rtsp, from
                    // 'rtsp://user:pass@host/...'"，明文帳密絕不能進到記錄或 API 回應。
                    _stderr.Append(line);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                _exitCode = process.ExitCode;
                State = PublisherState.Faulted;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        });

        return process;
    }

    /// <summary>標記已收到第一個封包。</summary>
    public void MarkStreaming()
    {
        if (State == PublisherState.Starting) State = PublisherState.Streaming;
    }

    /// <summary>
    /// 停止 publisher。回傳是否成功讓進程結束。
    /// </summary>
    public async Task<bool> StopAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return true;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            State = PublisherState.Stopped;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 進程已死或權限不足：仍然視為已停止，否則呼叫端會卡住。
            State = PublisherState.Faulted;
            return false;
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}

/// <summary>ffmpeg 啟動失敗（找不到執行檔等）。</summary>
public sealed class PublisherStartException : Exception
{
    public PublisherStartException(string message) : base(message)
    {
    }
}
