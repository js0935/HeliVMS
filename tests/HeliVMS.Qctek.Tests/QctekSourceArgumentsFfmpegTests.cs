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

    [SkippableFact]
    public void MJPEG來源轉碼後的輸出是每個關鍵影格都帶SPSPPS的AnnexB()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"helivms-qctek-transcode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var mjpeg = Path.Combine(directory, "input.avi");
            if (!TryRun(
                    ["ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=15",
                     "-t", "2", "-c:v", "mjpeg", "-q:v", "5", "-f", "avi", "-y", mjpeg],
                    out _))
            {
                Skip.If(true, "PATH 上沒有可用的 ffmpeg（或 lavfi／mjpeg 不可用），略過轉碼檢查。");
            }

            // 反向對照：舊的 -c:v copy 對 MJPEG 來源必然讓 h264 muxer 在寫 header 時失敗、
            // stdout 零位元組。少了這一段，「轉碼成功」可能只是這台機器的 ffmpeg 突然容忍 MJPEG，
            // 而不是我們真的修好了。
            // 用檔案載體重現，但把 bsf 換成即時來源的 dump_extra——現場錯誤是
            // 「h264 muxer supports only codec h264」，而檔案來源的 h264_mp4toannexb
            // 會先以「Codec 'mjpeg' is not supported」檔掉，看不到同一行錯誤。
            var remux = new List<string> { "ffmpeg" };
            remux.AddRange(QctekSourceArguments.Build(mjpeg));
            var bsf = remux.IndexOf("-bsf:v");
            Assert.True(bsf > 0);
            remux[bsf + 1] = "dump_extra";
            Assert.False(TryRunToFile(remux, Path.Combine(directory, "remux.h264"), out var remuxStderr),
                "MJPEG 來源用 -c:v copy 竟然成功：這台機器的 ffmpeg 行為與現場不同，本測試無效。");
            Assert.Contains("h264 muxer supports only codec h264", remuxStderr);

            // 正向：探測到 mjpeg 時必須即時轉碼，而且輸出是 QCC 能從關鍵影格開始解的 Annex-B。
            var transcoded = Path.Combine(directory, "transcoded.h264");
            var arguments = new List<string> { "ffmpeg" };
            arguments.AddRange(QctekSourceArguments.Build(mjpeg, 0, "mjpeg"));
            Assert.True(TryRunToFile(arguments, transcoded, out var transcodeStderr),
                $"MJPEG 轉 H.264 失敗：{transcodeStderr}");

            var bytes = File.ReadAllBytes(transcoded);
            Assert.True(bytes.Length > 0, "轉碼成功但 stdout 零位元組：CMS 仍然只會收到狀態幀。");

            var nals = ParseAnnexB(bytes);
            Assert.True(nals.Contains(7), "沒有 SPS：QCC 無法從關鍵影格開始解碼。");
            Assert.True(nals.Contains(8), "沒有 PPS：QCC 無法從關鍵影格開始解碼。");
            Assert.True(nals.Contains(5), "沒有 IDR：整段只有 P 框，畫面永遠起不來。");

            // libx264 的 raw h264 輸出會在每個 IDR 前寫 SPS/PPS，dump_extra 再補一層；
            // QCC 以「block1 size > 0」認關鍵影格，SPS 缺席的 IDR 會被它當非關鍵丟棄。
            for (var i = 0; i < nals.Count; i++)
            {
                if (nals[i] != 5)
                {
                    continue;
                }

                var preceding = nals.Skip(Math.Max(0, i - 4)).Take(4).ToList();
                Assert.True(preceding.Contains(7), $"第 {i} 個 IDR 前沒有 SPS，QCC 會把它當非關鍵影格丟掉。");
                Assert.True(preceding.Contains(8), $"第 {i} 個 IDR 前沒有 PPS，QCC 會把它當非關鍵影格丟掉。");
            }
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

    /// <summary>跑一次 ffmpeg，把 stdout 寫進檔案並回傳 stderr（命令列驗證要看 muxer 的錯誤）。</summary>
    private static bool TryRunToFile(IReadOnlyList<string> arguments, string outputPath, out string stderr)
    {
        stderr = string.Empty;
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

            var stderrTask = process.StandardError.ReadToEndAsync();
            using (var file = File.Create(outputPath))
            {
                process.StandardOutput.BaseStream.CopyTo(file);
            }

            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            stderr = stderrTask.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>讀出 Annex-B 串流每個 NAL 的 type（SPS=7、PPS=8、IDR=5）。</summary>
    private static List<int> ParseAnnexB(byte[] data)
    {
        var nals = new List<int>();
        var index = 0;
        while (index < data.Length - 3)
        {
            int start;
            if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1)
            {
                start = index + 3;
            }
            else if (index < data.Length - 4 && data[index] == 0 && data[index + 1] == 0 &&
                     data[index + 2] == 0 && data[index + 3] == 1)
            {
                start = index + 4;
            }
            else
            {
                index++;
                continue;
            }

            var end = start;
            while (end < data.Length - 3 &&
                   !(data[end] == 0 && data[end + 1] == 0 && data[end + 2] == 1) &&
                   !(end < data.Length - 4 && data[end] == 0 && data[end + 1] == 0 &&
                     data[end + 2] == 0 && data[end + 3] == 1))
            {
                end++;
            }

            if (start < end)
            {
                nals.Add(data[start] & 0x1f);
            }

            index = end;
        }

        return nals;
    }
}
