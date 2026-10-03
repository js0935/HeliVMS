using System.ComponentModel;
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
    private readonly Func<string, CancellationToken, Task<FfmpegEncoderStatus>>? _probeEncoder;
    private readonly RedactingErrorBuffer _stderr = new();
    private Process? _process;

    // 預設就是「沒有結束碼」：publisher 從未啟動過時若回傳 0，
    // 上層的錯誤訊息會說「ffmpeg 已結束（代碼 0）」，把「還沒跑」講成「正常結束」。
    private int _exitCode = int.MinValue;

    /// <param name="options">M244 設定；主要用 <see cref="WhepOptions.FfmpegPath"/>。</param>
    /// <param name="encode">ffmpeg 參數來源。</param>
    /// <param name="probeEncoder">
    /// 編碼器探測；傳 <c>null</c> 時走預設的 <see cref="FfmpegEncoderProbe"/>。
    /// 測試注入假探測，以免結果取決於 runner 上的 ffmpeg 建置。
    /// </param>
    public LivePublisher(
        WhepOptions options,
        LiveEncodeOptions encode,
        Func<string, CancellationToken, Task<FfmpegEncoderStatus>>? probeEncoder = null)
    {
        _options = options;
        _encode = encode;
        _probeEncoder = probeEncoder;
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

        // 先確認這台 ffmpeg 真的編得出 H.264，再去碰攝影機。少了這一步，缺 libx264 的
        // 部署會走完整條路：連 RTSP、開始讀串流、然後才因為「Unknown encoder」退出——
        // 使用者等滿 PublisherStartTimeout 才看到一段 ffmpeg stderr。
        EnsureEncoder(_options.FfmpegPath);

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

        // 「找不到執行檔」是這一整套功能最常見的部署失敗，而 Process.Start 丟出來的是
        // Win32Exception（訊息只有「系統找不到指定的檔案」），對維運毫無意義：看不出該動
        // 哪個設定。轉成帶環境變數名的 PublisherStartException，端點才回得出可行動的
        // 502。訊息只帶 ffmpeg 路徑，不帶 RTSP 帳密。
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            throw new PublisherStartException(
                $"無法啟動 ffmpeg（{WhepOptions.Prefix}FFMPEG={_options.FfmpegPath}）：{ex.Message}");
        }

        if (process is null)
        {
            throw new PublisherStartException(
                $"無法啟動 ffmpeg（{WhepOptions.Prefix}FFMPEG={_options.FfmpegPath}）：找不到執行檔。");
        }

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
    /// 缺視訊編碼器時立刻失敗，並說明該動哪裡。
    /// <para>
    /// 只有 <see cref="FfmpegEncoderStatus.Missing"/> 會擋：那是「ffmpeg 跑得起來但沒有
    /// libx264」，也就是換一個 ffmpeg build 就會好的情況，講清楚最省事。
    /// <see cref="FfmpegEncoderStatus.Unavailable"/>（問不到，例如
    /// <c>HELIVMS_WHEP_FFMPEG</c> 指向不存在的檔案）<b>不</b>擋，刻意讓
    /// <see cref="Process.Start(ProcessStartInfo)"/> 丟出原本那則帶環境變數名的訊息——
    /// 那才是對的診斷。
    /// </para>
    /// <para>
    /// 這裡是同步等待：探測結果依路徑快取，只有第一次真的開行程，而第一次必然發生在
    /// 有人按「開啟即時畫面」的當下——此時已經在等 ffmpeg 啟動了，多等一次探測不會讓
    /// 使用者感覺到差別。
    /// </para>
    /// </summary>
    private void EnsureEncoder(string ffmpegPath)
    {
        var probe = _probeEncoder
            ?? new FfmpegEncoderProbe(ffmpegPath).ProbeAsync;

        var status = probe(LiveEncodeOptions.VideoEncoder, CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        if (status != FfmpegEncoderStatus.Missing) return;

        throw new PublisherStartException(
            $"{ffmpegPath} 沒有 {LiveEncodeOptions.VideoEncoder} 編碼器，無法產生 H.264："
            + "請改用含 libx264 的 ffmpeg（Debian／Ubuntu 可安裝 ffmpeg-full），"
            + $"或用 {WhepOptions.Prefix}FFMPEG 指向另一個完整版 ffmpeg。");
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
