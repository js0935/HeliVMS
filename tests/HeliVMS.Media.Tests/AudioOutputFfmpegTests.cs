using System.Diagnostics;
using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 「解碼行程的第二個輸出」真的會被本機的 ffmpeg 接受，而且資料真的到得了接收端。
/// </summary>
/// <remarks>
/// <para>
/// 字串斷言擋不住版本漂移：<c>-flush_packets</c> 這類選項在 <c>ffmpeg -h full</c> 的清單裡
/// 根本不會出現（它是封裝器選項），所以沒有辦法用「你認得這個選項嗎」來問；但它一旦不被
/// 接受，收音就是整條無聲，而日誌只有一行錯誤。這裡用一個合成來源（<c>anullsrc</c>）跑
/// <b>正式產品用的同一份參數片段</b>（<see cref="AudioOutputArguments.Build"/>），
/// 一路驗到「接收端真的收到 1 秒的 PCM」。
/// </para>
/// <para>
/// 合成來源取代不了攝影機，但它驗的是參數與 socket 這一段——也就是換 ffmpeg 版本或改參數
/// 時真正會壞掉的地方。
/// </para>
/// </remarks>
[Collection(MediaIoCollection.Name)]
public sealed class AudioOutputFfmpegTests
{
    [SkippableFact]
    public async Task 正式的收音參數會把PCM送到接收端()
    {
        var help = FfmpegInstall.Help(FfmpegLocator.Ffmpeg);
        Skip.If(help is null, "PATH 上沒有可啟動的 ffmpeg，略過收音參數的實跑驗證。");

        using var receiver = new AudioStreamReceiver();
        var chunks = 0;
        var bytes = 0;
        receiver.ChunkReady += (_, chunk) =>
        {
            Interlocked.Increment(ref chunks);
            Interlocked.Add(ref bytes, chunk.Length);
        };
        receiver.Start();

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegLocator.Ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("anullsrc=r=16000:cl=mono");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("1");
        foreach (var arg in AudioOutputArguments.Build(receiver.Port, 16000))
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)!;
        var stderr = await proc.StandardError.ReadToEndAsync();
        Assert.True(proc.WaitForExit(20_000), "ffmpeg 逾時未結束。");
        Assert.Equal(0, proc.ExitCode);

        // 16 kHz × 16 bit × 單聲道 × 1 秒 ＝ 32000 bytes ＝ 10 個 100 ms 片段。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref bytes) < 32000 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        receiver.Stop();

        Assert.True(
            Volatile.Read(ref bytes) >= 32000 - receiver.ChunkBytes,
            $"只收到 {Volatile.Read(ref bytes)} bytes 的 PCM（預期約 32000）。ffmpeg stderr：{stderr}");
        Assert.True(Volatile.Read(ref chunks) >= 8, $"只收到 {Volatile.Read(ref chunks)} 個片段。");
        Assert.Empty(stderr.Trim());
    }
}
