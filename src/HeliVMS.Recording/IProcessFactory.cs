using System.Diagnostics;

namespace HeliVMS.Recording;

/// <summary>
/// 可注入的 ffmpeg 行程工廠，用於將行程啟動與外部世界隔離，以利單元測試。
/// </summary>
public interface IProcessFactory
{
    /// <summary>建立並啟動一個行程。</summary>
    IProcess Start(ProcessStartInfo startInfo);
}

/// <summary>行程抽象，以便在測試中模擬。</summary>
public interface IProcess : IAsyncDisposable, IDisposable
{
    /// <summary>標準輸出串流。</summary>
    StreamReader StandardOutput { get; }

    /// <summary>標準錯誤串流。</summary>
    StreamReader StandardError { get; }

    /// <summary>行程是否仍在執行中。</summary>
    bool HasExited { get; }

    /// <summary>結束代碼。</summary>
    int ExitCode { get; }

    /// <summary>等候行程結束。</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>結束行程。</summary>
    void Kill();
}

/// <summary>預設 ffmpeg 行程工廠。</summary>
public sealed class DefaultProcessFactory : IProcessFactory
{
    public IProcess Start(ProcessStartInfo startInfo)
    {
        var proc = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 ffmpeg 錄影程序。");
        return new SystemProcess(proc);
    }

    private sealed class SystemProcess : IProcess
    {
        private readonly System.Diagnostics.Process _proc;

        public SystemProcess(System.Diagnostics.Process proc)
        {
            _proc = proc;
        }

        public StreamReader StandardOutput => _proc.StandardOutput;

        public StreamReader StandardError => _proc.StandardError;

        public bool HasExited => _proc.HasExited;

        public int ExitCode => _proc.HasExited ? _proc.ExitCode : 0;

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
            => _proc.WaitForExitAsync(cancellationToken);

        public void Kill()
        {
            try
            {
                if (!_proc.HasExited)
                {
                    _proc.Kill();
                }
            }
            catch
            {
                // 測試或清理時不應拋出例外
            }
        }

        public void Dispose()
        {
            if (!_proc.HasExited)
            {
                try
                {
                    _proc.Kill();
                }
                catch
                {
                }
            }

            _proc.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_proc.HasExited)
            {
                try
                {
                    _proc.Kill();
                }
                catch
                {
                }
            }

            _proc.Dispose();
            await Task.CompletedTask;
        }
    }
}