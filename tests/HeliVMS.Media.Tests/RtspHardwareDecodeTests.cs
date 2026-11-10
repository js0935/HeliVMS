using System.Text;
using HeliVMS.Media;

namespace HeliVMS.Media.Tests;

/// <summary>
/// <see cref="RtspClient"/> 真的照 <see cref="HardwareDecodePolicy"/> 的決定啟動 ffmpeg。
/// </summary>
/// <remarks>
/// <para>
/// 政策的純函式測試（<see cref="HardwareDecodePolicyTests"/>）只能證明規則本身對；
/// 「有沒有接上」只能在實際啟動解碼行程之後看命令列。這一層漏接的症狀最貴：
/// 設定頁顯示 auto、畫面正常、CPU 卻多一倍，而且日誌乾淨。
/// </para>
/// <para>
/// 用 stub ffmpeg（.cmd）跑真的 <see cref="RtspClient"/>：探測回 64×36，解碼一律輸出
/// 呼叫端算好的那一幀，並把命令列寫進 args.txt。因此這是行程相關測試，必須與其他
/// 行程測試同一個集合（見 <see cref="MediaIoCollection"/>）。
/// </para>
/// </remarks>
[Collection(MediaIoCollection.Name)]
public class RtspHardwareDecodeTests
{
    [Fact]
    public async Task auto在縮小解碼時帶入明確的硬體解碼方法()
    {
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution64x36);
        WriteFrame(dir, 32 * 18 * 3);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", HwAccelStub(hwAccels: ["d3d11va", "dxva2"]));

        await using var client = NewClient(ffmpeg, probe, "auto");
        await client.SetDecodeWidthAsync(32);

        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameDecoded += (_, _) => got.TrySetResult();
        await client.StartAsync();

        var done = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(done == got.Task, "auto 在縮小解碼時應正常出畫");

        var args = await File.ReadAllTextAsync(Path.Combine(dir, "args.txt"));
        Assert.Contains("-hwaccel d3d11va", args);
        Assert.Equal("d3d11va", client.HwAccelInUse);
        Assert.True(client.HwAccelSupported, "這一輪真的用了硬解，HwAccelSupported 應為 true");

        await client.StopAsync();
    }

    [Fact]
    public async Task 原生尺寸的H264以軟解啟動()
    {
        // 實測原生 1080p H.264：軟解 25.57s vs d3d11va 26.17s——多一層 GPU 依賴卻沒有好處。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution64x36);
        WriteFrame(dir, 64 * 36 * 3);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", HwAccelStub(hwAccels: ["d3d11va", "dxva2"]));

        await using var client = NewClient(ffmpeg, probe, "auto");

        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameDecoded += (_, _) => got.TrySetResult();
        await client.StartAsync();

        var done = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(done == got.Task, "auto 在原生尺寸時應以軟解出畫");

        var args = await File.ReadAllTextAsync(Path.Combine(dir, "args.txt"));
        Assert.DoesNotContain("-hwaccel", args);
        Assert.Null(client.HwAccelInUse);
        Assert.False(client.HwAccelSupported, "原生尺寸下 auto 刻意不啟用硬解");

        await client.StopAsync();
    }

    [Fact]
    public async Task 原生尺寸的HEVC仍會帶入硬體解碼()
    {
        // 實測原生 1080p HEVC：軟解 40.42/46.86s → d3d11va 35.47/36.28s（−12%～−23%），
        // 10-bit 更明顯（62.73/84.36s → 41.66/46.03s）。少了這條，「不縮尺寸」的 H.265
        // 頻道會一律軟解，白白多燒 CPU 而畫面完全正常——正是這一層守門要抓的漏接。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution64x36Hevc);
        WriteFrame(dir, 64 * 36 * 3);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", HwAccelStub(hwAccels: ["d3d11va", "dxva2"]));

        await using var client = NewClient(ffmpeg, probe, "auto");

        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameDecoded += (_, _) => got.TrySetResult();
        await client.StartAsync();

        var done = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(done == got.Task, "原生尺寸的 HEVC 應正常出畫");

        var args = await File.ReadAllTextAsync(Path.Combine(dir, "args.txt"));
        Assert.Contains("-hwaccel d3d11va", args);
        Assert.Equal("d3d11va", client.HwAccelInUse);
        Assert.True(client.HwAccelSupported, "原生尺寸的 HEVC 這一輪真的用了硬解");

        await client.StopAsync();
    }

    [Fact]
    public async Task auto在探測不到偏好方法時以軟解啟動()
    {
        // 探測得到 vaapi（Linux 的方法，Windows ffmpeg 只在特定組建中出現）：偏好清單裡沒有它，
        // 不可以「隨便挑一個」——挑錯的症狀是連得上卻沒畫面。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution64x36);
        WriteFrame(dir, 32 * 18 * 3);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", HwAccelStub(hwAccels: ["vaapi"]));

        await using var client = NewClient(ffmpeg, probe, "auto");
        await client.SetDecodeWidthAsync(32);

        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameDecoded += (_, _) => got.TrySetResult();
        await client.StartAsync();

        var done = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(done == got.Task, "沒有可用的偏好方法時應以軟解出畫");

        var args = await File.ReadAllTextAsync(Path.Combine(dir, "args.txt"));
        Assert.DoesNotContain("-hwaccel", args);
        Assert.Null(client.HwAccelInUse);

        await client.StopAsync();
    }

    [Fact]
    public async Task 明確指定的值在原生尺寸也照用()
    {
        // 指定值＝使用者已表明要用它；政策只檢查本機支援與否，不因「原生尺寸」改主意。
        var dir = NewDir();
        var probe = WriteStub(dir, "ffprobe.cmd", EchoResolution64x36);
        WriteFrame(dir, 64 * 36 * 3);
        var ffmpeg = WriteStub(dir, "ffmpeg.cmd", HwAccelStub(hwAccels: ["d3d11va"]));

        await using var client = NewClient(ffmpeg, probe, "d3d11va");

        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameDecoded += (_, _) => got.TrySetResult();
        await client.StartAsync();

        var done = await Task.WhenAny(got.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(done == got.Task, "明確指定 d3d11va 時應正常出畫");

        var args = await File.ReadAllTextAsync(Path.Combine(dir, "args.txt"));
        Assert.Contains("-hwaccel d3d11va", args);
        Assert.Equal("d3d11va", client.HwAccelInUse);

        await client.StopAsync();
    }

    private static RtspClient NewClient(string ffmpeg, string probe, string hwAccel)
        => new("rtsp://10.0.0.1/live.sdp", ffmpeg, probe, hwAccel: hwAccel)
        {
            ProbeTimeout = TimeSpan.FromSeconds(5),
            FirstFrameTimeout = TimeSpan.FromSeconds(10),
            FrameTimeout = TimeSpan.FromSeconds(10),
            MaxFramesPerSecond = 30,
        };

    private const string EchoResolution64x36 =
        """
        @echo off
        echo {"streams":[{"codec_type":"video","codec_name":"h264","width":64,"height":36,"r_frame_rate":"1/1"}]}
        """;

    private const string EchoResolution64x36Hevc =
        """
        @echo off
        echo {"streams":[{"codec_type":"video","codec_name":"hevc","width":64,"height":36,"r_frame_rate":"1/1"}]}
        """;

    // 第一參數固定 -hide_banner，故探測呼叫的 %2 為 -hwaccels（見 FfmpegHwAccel.Probe）。
    private static string HwAccelStub(string[] hwAccels)
    {
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine("if \"%2\"==\"-hwaccels\" (");
        builder.AppendLine("  echo Hardware acceleration methods:");
        foreach (var method in hwAccels)
        {
            builder.AppendLine($"  echo {method}");
        }

        builder.AppendLine("  exit /b 0");
        builder.AppendLine(")");
        builder.AppendLine("echo %* > \"%~dp0args.txt\"");
        builder.AppendLine("type \"%~dp0frame.bin\"");
        builder.AppendLine("ping -n 30 127.0.0.1 >nul");
        return builder.ToString();
    }

    private static void WriteFrame(string dir, int bytes)
        => File.WriteAllBytes(Path.Combine(dir, "frame.bin"), new byte[bytes]);

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"helivms-rtsp-hw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteStub(string dir, string name, string body)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, body, Encoding.ASCII);
        return path;
    }
}
