using System.Diagnostics;
using HeliVMS.Qctek.Live;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 用真的 ffmpeg 驗證「錄影檔來源會照畫面速率輸出」。
/// </summary>
/// <remarks>
/// <para>
/// 引數純函式的測試只能證明 <c>-re</c> 在清單裡、在 <c>-i</c> 之前；「它真的有效」只有
/// 量時間能證明。少了 <c>-re</c> 的症狀是 QCC 回放進度條瞬間跑到尾端、畫面跳格，
/// 伺服器端完全沒有錯誤——所以這裡直接跑一次 2 秒的素材，量它花多久讀完。
/// </para>
/// <para>
/// 同一支測試也做反向對照（拿掉 <c>-re</c> 必須明顯變快）：否則在「機器慢到連全速
/// 讀都要兩秒」的情況下，正向斷言會永遠綠而甚麼都沒驗到。
/// </para>
/// </remarks>
public sealed class QctekSourceArgumentsFfmpegTests
{
    [SkippableFact]
    public void 錄影檔來源帶re時以畫面速率輸出而不是磁碟速率()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"helivms-qctek-pace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var file = Path.Combine(directory, "input.mp4");
            if (!TryRun(
                    ["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=15",
                     "-t", "2", "-pix_fmt", "yuv420p", "-y", file],
                    out _))
            {
                Skip.If(true, "PATH 上沒有可用的 ffmpeg（或 lavfi 不可用），略過回放節奏檢查。");
            }

            var arguments = new List<string> { "ffmpeg" };
            arguments.AddRange(QctekSourceArguments.Build(file));

            Assert.True(TryRun(arguments, out var paced), "ffmpeg 讀取錄影檔失敗");
            Assert.True(
                paced >= TimeSpan.FromSeconds(1.2),
                $"2 秒素材只花了 {paced.TotalSeconds:0.00} 秒讀完：-re 沒有生效，QCC 的回放進度條會瞬間跑到尾端。");

            // 反向對照：沒有 -re 時同一段素材必須明顯快於畫面長度。
            var unpaced = arguments.Where(argument => argument != "-re").ToList();
            Assert.True(TryRun(unpaced, out var fast), "拿掉 -re 之後 ffmpeg 反而失敗");
            Assert.True(
                fast < TimeSpan.FromSeconds(1.2),
                $"拿掉 -re 仍花了 {fast.TotalSeconds:0.00} 秒：這台機器量不出節奏差異，本測試無效。");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // 暫存目錄清不掉不影響結論。
            }
        }
    }

    /// <summary>啟動一個行程、讀完 stdout（不讀會讓 pipe 滿而卡住），回報是否成功與耗時。</summary>
    private static bool TryRun(IReadOnlyList<string> arguments, out TimeSpan elapsed)
    {
        elapsed = default;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = arguments[0],
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments.Skip(1))
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            var stderr = process.StandardError.ReadToEndAsync();
            var stopwatch = Stopwatch.StartNew();
            process.StandardOutput.BaseStream.CopyTo(Stream.Null);
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            stopwatch.Stop();
            _ = stderr.Result;
            elapsed = stopwatch.Elapsed;
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            // ffmpeg 不在 PATH（Win32Exception）：呼叫端自我略過。
            return false;
        }
    }
}
