using System.Diagnostics;
using System.Text;
using HeliVMS.Media;
using HeliVMS.Recording;
using HeliVMS.Shared.Models;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 回放端真的把「編碼格式」交給硬體解碼政策，而不只是叫了同一個函式。
/// </summary>
/// <remarks>
/// <para>
/// 政策本身（`HardwareDecodePolicyTests`）已釘住「原生尺寸的 HEVC 值得硬解、
/// H.264 不值得」；這裡守的是**輸入有沒有傳對**。漏傳 <c>videoCodec</c> 的症狀最貴：
/// 單格回放一段 H.265 錄影時一律軟解，CPU 比即時監看多 12–45%，而兩邊畫面都完全正常、
/// 日誌也乾淨——只有量測或看工作管理員才發現。
/// </para>
/// <para>
/// 用 stub <c>ffmpeg.cmd</c> 只為回答 <c>-hwaccels</c>（探測結果依路徑快取），解碼行程本身
/// 由注入的行程工廠取代，因此不需要真的 ffmpeg；但探測會啟動一個行程，故與其他行程測試
/// 同一個集合。
/// </para>
/// </remarks>
[Collection(RecordingIoCollection.Name)]
public sealed class PlaybackHwAccelWiringTests
{
    private static readonly StreamProbeInfo Hevc = new(64, 36, "hevc", null, 25);
    private static readonly StreamProbeInfo H264 = new(64, 36, "h264", null, 25);

    /// <summary>原生尺寸的 H.265 錄影要帶硬體解碼：這是量到 12–45% 的那一格。</summary>
    [Fact]
    public async Task 原生尺寸的HEVC回放會帶入硬體解碼()
    {
        var args = await StartArgsAsync(Hevc, displayWidth: 0);

        Assert.Equal("d3d11va", ValueAfter(args, "-hwaccel"));
    }

    /// <summary>原生尺寸的 H.264 維持軟解：多一層 GPU 依賴卻量不到好處。</summary>
    [Fact]
    public async Task 原生尺寸的H264回放維持軟解()
    {
        var args = await StartArgsAsync(H264, displayWidth: 0);

        Assert.DoesNotContain("-hwaccel", args);
    }

    /// <summary>縮尺寸時不看格式：H.264 也照用（那是量到 52–70% 的那一條）。</summary>
    [Fact]
    public async Task 縮小解碼的回放不看編碼格式()
    {
        var args = await StartArgsAsync(H264, displayWidth: 32);

        Assert.Equal("d3d11va", ValueAfter(args, "-hwaccel"));
    }

    private static async Task<IList<string>> StartArgsAsync(StreamProbeInfo info, int displayWidth)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helivms-play-hw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "seg-000000000.mp4");
        File.WriteAllBytes(file, []);
        var ffmpeg = Path.Combine(dir, "ffmpeg.cmd");
        File.WriteAllText(
            ffmpeg,
            """
            @echo off
            if "%2"=="-hwaccels" (
              echo Hardware acceleration methods:
              echo d3d11va
              echo dxva2
              exit /b 0
            )
            """,
            Encoding.ASCII);

        var factory = new RecordingFactory();
        await using var session = new PlaybackSession(
            new SegmentRecord
            {
                Id = 1,
                ChannelId = 1,
                Stream = "main",
                StartUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                FilePath = file,
                DurationSec = 10,
                Status = SegmentStatus.Final,
            },
            ffmpegPath: ffmpeg,
            processFactory: factory,
            probe: _ => info,
            frameTimes: _ => [])
        {
            HwAccel = "auto",
            DisplayWidth = displayWidth,
        };

        await session.PlayAsync();

        return factory.Started.Single().StartInfo.ArgumentList;
    }

    private static string? ValueAfter(IList<string> args, string flag)
    {
        var index = args.IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private sealed class RecordingFactory : IProcessFactory
    {
        public List<FakeProcess> Started { get; } = [];

        public IProcess Start(ProcessStartInfo startInfo)
        {
            // 一定要吐出一格：PlaybackSession 把「帶了 -hwaccel 卻一幀都沒出」當成加速方式
            // 不可用，會啟動 5 分鐘的**行程級靜態**冷卻期，後面每一個測試就都拿不到硬解
            // （症狀是同一支測試單獨跑綠、整個類別跑紅）。
            var process = new FakeProcess(new MemoryStream(new byte[8192])) { StartInfo = startInfo };
            Started.Add(process);
            return process;
        }
    }

    /// <summary>立即結束、零影格的回放行程；只為了讀回實際下給 ffmpeg 的參數。</summary>
    private sealed class FakeProcess : IProcess
    {
        public FakeProcess(Stream standardOutput)
        {
            StandardOutput = new StreamReader(standardOutput, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            StandardError = new StreamReader(
                new MemoryStream(),
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false);
        }

        public StreamReader StandardOutput { get; }

        public StreamReader StandardError { get; }

        public ProcessStartInfo StartInfo { get; set; } = new();

        public bool HasExited => true;

        public int ExitCode => 0;

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RequestQuit()
        {
        }

        public void Kill()
        {
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
