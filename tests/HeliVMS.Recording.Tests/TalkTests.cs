using System.Diagnostics;
using System.Text;
using HeliVMS.Recording.Talk;
using HeliVMS.Shared;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 對講（雙向語音，§14.1 #11）後端縫的行為。
///
/// 這條路徑過去在程式碼裡完全不存在——<c>ITalkProvider</c> 只寫在藍圖，零引用。
/// 這裡把「挑供應商 → 組 ffmpeg 指令 → 啟停子行程 → 記入行程表」每一段都釘住。
/// 對講的失敗模式特別安靜：子行程啟動、連線、送完，卻沒有聲音且日誌乾淨，
/// 沒有斷言就只有在現場才聽得出來。
///
/// 全程不需要真的 ffmpeg 或真的攝影機：行程工廠是可注入的縫（見 <see cref="IProcessFactory"/>）。
/// </summary>
public sealed class TalkTests
{
    private const int ChannelId = 5;
    private const string ChannelUrl = "rtsp://cam.example.invalid/stream1";
    private const string AudioFile = "C:\\audio\\announce.wav";

    [Fact]
    public void 明確指定的對講位址優先於頻道位址()
    {
        var registry = new TalkProviderRegistry();
        var request = new TalkRequest(
            ChannelId, ChannelUrl, AudioFile, PreferredTalkUrl: " rtsp://cam.example.invalid/talk ");

        var provider = registry.Resolve(request);

        Assert.NotNull(provider);
        Assert.Equal("explicit-url", provider!.Name);
        Assert.Equal("rtsp://cam.example.invalid/talk", provider.Resolve(request).Url);
    }

    [Fact]
    public void 未指定對講位址時退回頻道自身的_RTSP_位址()
    {
        var registry = new TalkProviderRegistry();
        var request = new TalkRequest(ChannelId, ChannelUrl, AudioFile);

        var provider = registry.Resolve(request);

        Assert.NotNull(provider);
        Assert.Equal("rtsp-backchannel", provider!.Name);
        Assert.Equal(ChannelUrl, provider.Resolve(request).Url);
    }

    [Fact]
    public void 供應商清單的順序即優先序()
    {
        var registry = new TalkProviderRegistry(
        [
            new GenericRtspTalkProvider(),
            new ExplicitTalkUrlProvider(),
        ]);
        var request = new TalkRequest(
            ChannelId, ChannelUrl, AudioFile, PreferredTalkUrl: "rtsp://cam.example.invalid/talk");

        Assert.Equal("rtsp-backchannel", registry.Resolve(request)!.Name);
    }

    [Fact]
    public void 空的供應商清單解析為_null()
    {
        var registry = new TalkProviderRegistry([]);

        Assert.Null(registry.Resolve(new TalkRequest(ChannelId, ChannelUrl, AudioFile)));
    }

    [Fact]
    public void 對講指令包含編碼與_RTSP_回傳通道參數()
    {
        var target = new TalkTarget("rtsp://cam.example.invalid/talk", TalkTransport.RtspBackchannel, "explicit-url");

        var command = TalkCommandBuilder.Build(AudioFile, target);
        var args = command.Arguments.ToList();

        Assert.Equal(FfmpegLocator.Ffmpeg, command.FileName);
        Assert.Equal("rtsp://cam.example.invalid/talk", command.TargetUrl);
        Assert.Contains("-re", args);
        Assert.Contains("-i", args);
        Assert.Equal(AudioFile, args[args.IndexOf("-i") + 1]);
        Assert.Equal("1", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("8000", args[args.IndexOf("-ar") + 1]);
        Assert.Equal("pcm_alaw", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal("tcp", args[args.IndexOf("-rtsp_transport") + 1]);
        Assert.Equal("rtsp", args[args.IndexOf("-f") + 1]);
        Assert.Equal("rtsp://cam.example.invalid/talk", args[^1]);
    }

    [Fact]
    public void 空白的音訊檔路徑會被拒絕()
    {
        var target = new TalkTarget(ChannelUrl, TalkTransport.RtspBackchannel, "rtsp-backchannel");

        Assert.Throws<ArgumentException>(() => TalkCommandBuilder.Build("   ", target));
    }

    [Fact]
    public void 未知的編碼會被拒絕()
    {
        var target = new TalkTarget(ChannelUrl, TalkTransport.RtspBackchannel, "rtsp-backchannel");

        Assert.Throws<ArgumentOutOfRangeException>(() => TalkCommandBuilder.Build(AudioFile, target, (TalkCodec)99));
    }

    [Fact]
    public void 找不到供應商時啟動對講會擲例外()
    {
        var service = new TalkService(
            new TalkProviderRegistry([]),
            new FakeProcessFactory(psi => new FakeProcess(psi, exitCode: 0)),
            new FfmpegProcessRegistry());

        var request = new TalkRequest(ChannelId, ChannelUrl, AudioFile);

        Assert.Throws<InvalidOperationException>(() => service.Start(request));
    }

    [Fact]
    public void 啟動對講會把指令交給行程工廠並登錄到行程表()
    {
        var registry = new FfmpegProcessRegistry();
        var factory = new FakeProcessFactory(psi => new FakeProcess(psi, exitCode: 0));
        var service = new TalkService(new TalkProviderRegistry(), factory, registry);

        var session = service.Start(new TalkRequest(ChannelId, ChannelUrl, AudioFile));

        Assert.Equal(ChannelUrl, session.Target.Url);
        Assert.Equal("rtsp-backchannel", session.Target.Provider);
        var psi = Assert.Single(factory.Started);
        Assert.Equal(FfmpegLocator.Ffmpeg, psi.FileName);
        Assert.Equal("-re", psi.ArgumentList[3]);
        Assert.Equal(ChannelUrl, psi.ArgumentList[^1]);

        var snapshot = registry.Snapshot();
        Assert.Equal(1, snapshot.Running);
        Assert.Equal(1, snapshot.Other);
        Assert.Equal(1, snapshot.TotalStarted);
        Assert.Null(snapshot.LastFailureKey);
    }

    [Fact]
    public async Task 對講正常播完不會記為失敗()
    {
        var registry = new FfmpegProcessRegistry();
        var factory = new FakeProcessFactory(psi => new FakeProcess(psi, exitCode: 0, autoExit: true));
        var service = new TalkService(new TalkProviderRegistry(), factory, registry);

        var session = service.Start(new TalkRequest(ChannelId, ChannelUrl, AudioFile));
        var code = await session.WaitAsync();

        Assert.Equal(0, code);
        var snapshot = registry.Snapshot();
        Assert.Equal(0, snapshot.Running);
        Assert.Equal(1, snapshot.TotalFinished);
        Assert.Equal(0, snapshot.TotalFailed);
        Assert.Null(snapshot.LastFailureKey);
    }

    [Fact]
    public async Task 對講非零結束會記為失敗並附上_stderr()
    {
        var registry = new FfmpegProcessRegistry();
        var factory = new FakeProcessFactory(
            psi => new FakeProcess(psi, exitCode: 1, stderr: "對講連線失敗", autoExit: true));
        var service = new TalkService(new TalkProviderRegistry(), factory, registry);

        var session = service.Start(new TalkRequest(ChannelId, ChannelUrl, AudioFile));
        var code = await session.WaitAsync();

        Assert.Equal(1, code);
        var snapshot = registry.Snapshot();
        Assert.Equal(1, snapshot.TotalFailed);
        Assert.Equal("talk/ch005", snapshot.LastFailureKey);
        Assert.Contains("對講連線失敗", snapshot.LastFailure ?? string.Empty);
    }

    [Fact]
    public async Task 直接處置對講會中止子行程且不算失敗()
    {
        var registry = new FfmpegProcessRegistry();
        var fake = new FakeProcess(new ProcessStartInfo(), exitCode: 0);
        var factory = new FakeProcessFactory(_ => fake);
        var service = new TalkService(new TalkProviderRegistry(), factory, registry);

        var session = service.Start(new TalkRequest(ChannelId, ChannelUrl, AudioFile));
        await session.DisposeAsync();

        Assert.Equal(1, fake.KillCount);
        var snapshot = registry.Snapshot();
        Assert.Equal(0, snapshot.Running);
        Assert.Equal(1, snapshot.TotalFinished);
        Assert.Equal(0, snapshot.TotalFailed);
    }

    [Fact]
    public async Task 等候期間取消會終止行程並記為失敗()
    {
        var registry = new FfmpegProcessRegistry();
        var fake = new FakeProcess(new ProcessStartInfo(), exitCode: 0);
        var factory = new FakeProcessFactory(_ => fake);
        var service = new TalkService(new TalkProviderRegistry(), factory, registry);

        var session = service.Start(new TalkRequest(ChannelId, ChannelUrl, AudioFile));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WaitAsync(cts.Token));

        Assert.Equal(1, fake.KillCount);
        var snapshot = registry.Snapshot();
        Assert.Equal(1, snapshot.TotalFailed);
        Assert.Equal("talk/ch005", snapshot.LastFailureKey);
        Assert.Equal("對講已取消", snapshot.LastFailure);
    }

    private sealed class FakeProcessFactory : IProcessFactory
    {
        private readonly Func<ProcessStartInfo, FakeProcess> _make;

        public FakeProcessFactory(Func<ProcessStartInfo, FakeProcess> make) => _make = make;

        public List<ProcessStartInfo> Started { get; } = [];

        public IProcess Start(ProcessStartInfo startInfo)
        {
            Started.Add(startInfo);
            return _make(startInfo);
        }
    }

    private sealed class FakeProcess : IProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeProcess(ProcessStartInfo startInfo, int exitCode, string stderr = "", bool autoExit = false)
        {
            StartInfo = startInfo;
            ExitCode = exitCode;
            StandardError = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(stderr)));
            StandardOutput = new StreamReader(new MemoryStream());
            if (autoExit)
            {
                HasExited = true;
                _exited.TrySetResult();
            }
        }

        public ProcessStartInfo StartInfo { get; }

        public StreamReader StandardOutput { get; }

        public StreamReader StandardError { get; }

        public bool HasExited { get; private set; }

        public int ExitCode { get; }

        public int KillCount { get; private set; }

        public bool Disposed { get; private set; }

        public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
            => await _exited.Task.WaitAsync(cancellationToken);

        /// <summary>對講用的 ffmpeg 不吃 stdin 的 <c>q</c>；介面成員在此無事可做。</summary>
        public void RequestQuit()
        {
        }

        public void Kill()
        {
            KillCount++;
            HasExited = true;
            _exited.TrySetCanceled();
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
