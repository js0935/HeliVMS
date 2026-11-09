using System.Diagnostics;
using System.IO;
using HeliVMS.Qctek.Live;
using HeliVMS.Shared;

namespace HeliVMS.App.Services;

/// <summary>
/// 一路即時影像的 ffmpeg 來源：RTSP 拉流 → <c>-f h264</c> stdout（Annex-B 基本流）。
/// </summary>
/// <remarks>
/// <para>
/// 每頻道一個短生命週期的 ffmpeg 行程：CMS（keepalive 位圖）要哪幾路才開哪幾路，
/// 斷線即收——與排程截圖（<see cref="HeliVMS.Media.SnapshotGrabber"/>）同一個理由：便宜攝影機的
/// RTSP 工作階段有限，寧可每次重連，也不要長期占著。輸出是 <c>-c:v copy</c> 的純
/// remux：CMS 端自己解碼，伺服器不再花一分 CPU 解碼。
/// </para>
/// <para>
/// <c>-bsf:v dump_extra</c> 把 extradata（SPS/PPS）插在每個關鍵影格之前：部分攝影機
/// 只在 SDP 帶 sprop-parameter-sets、流內不再重複；而 QCC 以「block1 size > 0」當
/// 關鍵影格判準，SPS 缺席的關鍵影格會被它當非關鍵丟棄——那一路的畫面就永遠起不來
/// （黑屏，且沒有任何錯誤）。
/// </para>
/// </remarks>
internal sealed class QctekLiveStreamSource : IQctekLiveSource
{
    private readonly Process _process;
    private readonly string _source;
    private readonly Task _stderrDrain;

    private QctekLiveStreamSource(Process process, string source, Task stderrDrain)
    {
        _process = process;
        _source = source;
        _stderrDrain = stderrDrain;
        Output = process.StandardOutput.BaseStream;
    }

    public Stream Output { get; }

    /// <summary>
    /// 啟動 ffmpeg；啟動失敗（ffmpeg 不在、行程起不來）回 <c>null</c>——呼叫端會對
    /// 該頻道每秒回狀態幀，而不是讓 live 迴圈卡住。攝影機層的失敗（連不上、無授權）
    /// 要到 ffmpeg 跑起來之後才會發生，那時 stdout 沒有資料、狀態幀會說「尚無影格」。
    /// </summary>
    public static QctekLiveStreamSource? TryOpen(string url, Action<string>? log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var safe = RtspUri.Redact(url);

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.Ffmpeg,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // stdin 立即送 EOF（Start 後 Close）：不讓 ffmpeg 等互動輸入（App 是 GUI、
        // 也沒有人會去按「q」）。不用 -nostdin 選項——它是合法選項但不出現在
        // `ffmpeg -h full`，契約測試（FfmpegOptionLiteralsFfmpegTests）以 help 為準。
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
        {
            // RTSP 專用 demuxer 選項：對檔案來源會「Option not found」（與取影同一規則）。
            psi.ArgumentList.Add("-rtsp_transport");
            psi.ArgumentList.Add("tcp");
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(url);
        psi.ArgumentList.Add("-an");            // CMS 即時影像只要視訊
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("copy");           // 純 remux，不解碼
        psi.ArgumentList.Add("-bsf:v");
        psi.ArgumentList.Add("dump_extra");     // 關鍵影格前補 SPS/PPS（見型別說明）
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("h264");
        psi.ArgumentList.Add("-");

        try
        {
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start 回傳 null");
            process.StandardInput.Close();

            // stderr 的 pipe 必須持續讀掉：塞滿（64 KB）會讓 ffmpeg 卡在寫入，
            // 症狀是「行程在跑但永遠沒有輸出」。loglevel error 讓流量很小。
            // 前兩行一定要寫進診斷日誌：攝影機層的失敗（401、連不上、找不到串流）
            // 只出現在 stderr；不記的話症狀是「來源已啟動、狀態幀一直 0x8004、
            // 日誌乾淨」——CMS 回報「有連線、沒畫面」時最難查的就是這一種。
            // 只記前兩行：來源每 5 秒重試一次，全記會把 512 KB 的日誌洗掉。
            // 錯誤訊息會回顯輸入網址，一律經 RedactText 遮掉帳密。
            var drain = Task.Run(async () =>
            {
                var logged = 0;
                try
                {
                    while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        if (line.Length == 0 || logged >= 2)
                        {
                            continue;
                        }

                        logged++;
                        log?.Invoke($"QCTEK live：ffmpeg：{RtspUri.RedactText(line)}");
                    }
                }
                catch (Exception)
                {
                    // 行程被砍、pipe 關閉：drain 的正常結束方式。
                }
            });

            log?.Invoke($"QCTEK live：來源已啟動（{safe}，pid {process.Id}）");
            return new QctekLiveStreamSource(process, safe, drain);
        }
        catch (Exception ex)
        {
            log?.Invoke($"QCTEK live：來源啟動失敗（{safe}）：{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 行程已自己結束：不必處理。
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 逾時也放棄：行程已被 Kill，OS 會收尾；不讓來源清理卡住通道關閉。
        }

        Output.Dispose();
        _process.Dispose();

        try
        {
            await _stderrDrain.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
