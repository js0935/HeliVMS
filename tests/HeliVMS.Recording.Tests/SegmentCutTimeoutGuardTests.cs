using System.Diagnostics;
using HeliVMS.Recording;
using HeliVMS.Shared;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 收檔逾時的守衛（<see cref="SegmentRecorder.CutAsync"/>）。
///
/// 現場證據：%TEMP%\helivms-playback.log 有 164 筆「回放前收檔失敗：TaskCanceledException」
/// （2026-10-30 至 11-12，對應操作員每次開回放；收檔等於從未成功過）。HEAD 的
/// <see cref="SegmentRecorder.CutAsync"/> 以 <c>WaitAsync(timeout)</c> 等待並只接
/// <see cref="TimeoutException"/>——逾時回 false、不擲例外；本測試把這個形狀釘住，
/// 防止未來改回「CTS + WaitAsync(token)」那種會把逾時變成 TaskCanceledException 的寫法
/// （那正是現場 164 筆無名日誌的來源形狀）。
/// </summary>
public sealed class SegmentCutTimeoutGuardTests : IDisposable
{
    private const string RtspUrl = "rtsp://example.invalid/live";

    private readonly string _dbPath;
    private readonly string _recordingsRoot;
    private readonly SqliteStore _store;
    private readonly SegmentRepository _segments;
    private readonly int _channelId;

    public SegmentCutTimeoutGuardTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"helivms-cutguard-{Guid.NewGuid():N}.db");
        _recordingsRoot = Path.Combine(Path.GetTempPath(), $"helivms-cutguard-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_recordingsRoot);
        _store = new SqliteStore(_dbPath);
        _store.Initialize();
        _segments = new SegmentRepository(_store);
        _channelId = new ChannelRepository(_store).Add("測試頻道", RtspUrl);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(_recordingsRoot))
        {
            Directory.Delete(_recordingsRoot, recursive: true);
        }
    }

    /// <summary>stub ffmpeg：寫出輸出後結束，但「不理會 q」（現場卡住的攝影機就是這個樣子）。</summary>
    private static StubProcess IgnoreQuitProcess(ProcessStartInfo psi)
        => new(psi, exitCode: 0, ignoreQuit: true);

    [Fact]
    public async Task ffmpeg不理收尾請求時收檔逾時回false而不是TaskCanceledException()
    {
        var factory = new StubFactory(IgnoreQuitProcess);
        await using var recorder = new SegmentRecorder(_segments, factory, _ => string.Empty);
        await recorder.StartAsync(_channelId, RtspUrl, _recordingsRoot, "main", 42);

        // 等它真的在錄
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!recorder.IsRecording && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(recorder.IsRecording);

        // 段剛起，MinCutAge 擋下——不會走到等待。
        Assert.False(await recorder.CutAsync(TimeSpan.FromSeconds(2)));

        // 等過 MinCutAge（10 秒）再切：這次會送 q、stub 不理、等 2 秒逾時。
        // 逾時必須是「回 false」，不是 TaskCanceledException——後者會一路冒到
        // PlaybackRecordingFinalizer 的日誌，變成現場那種整頁無法定位的失敗行。
        await Task.Delay(10_500);
        Assert.False(await recorder.CutAsync(TimeSpan.FromSeconds(2)));
        await recorder.DisposeAsync();
    }

    private sealed class StubFactory : IProcessFactory
    {
        private readonly Func<ProcessStartInfo, StubProcess> _make;

        public StubFactory(Func<ProcessStartInfo, StubProcess> make) => _make = make;

        public IProcess Start(ProcessStartInfo startInfo) => _make(startInfo);
    }

    private sealed class StubProcess : IProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _ignoreQuit;
        private readonly string _outputPath;

        public StubProcess(ProcessStartInfo startInfo, int exitCode, bool ignoreQuit)
        {
            StartInfo = startInfo;
            _ignoreQuit = ignoreQuit;
            _outputPath = startInfo.ArgumentList[^1];
            ExitCode = exitCode;
            StandardError = new StreamReader(new MemoryStream());
            StandardOutput = new StreamReader(new MemoryStream());
        }

        public ProcessStartInfo StartInfo { get; }
        public byte[] Output { get; } = System.Text.Encoding.UTF8.GetBytes("stub");
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }
        public bool HasExited { get; private set; }
        public int ExitCode { get; }
        public int KillCount { get; private set; }
        public int QuitRequestCount { get; private set; }

        public void ProduceAndExit()
        {
            File.WriteAllBytes(_outputPath, Output);
            HasExited = true;
            _exited.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _exited.Task.WaitAsync(cancellationToken);

        public void RequestQuit()
        {
            QuitRequestCount++;
            if (!_ignoreQuit && !HasExited)
            {
                ProduceAndExit();
            }
        }

        public void Kill()
        {
            KillCount++;
            HasExited = true;
            _exited.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            _exited.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _exited.TrySetResult();
    }
}
