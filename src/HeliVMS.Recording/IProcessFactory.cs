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

    /// <summary>
    /// 請行程自己收尾：對 ffmpeg 送出 <c>q</c>，它會寫完當前 fragment／trailer 才結束。
    /// </summary>
    /// <remarks>
    /// 只有以 <see cref="ProcessStartInfo.RedirectStandardInput"/> 啟動的行程有效；其餘情況
    /// （未重導、管線已關、行程已結束）一律靜靜忽略，呼叫端仍須以 <see cref="Kill"/> 兜底。
    /// </remarks>
    void RequestQuit();

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

        public void RequestQuit()
        {
            try
            {
                if (_proc.HasExited)
                {
                    return;
                }

                // ffmpeg 的 'q' 是「收尾後結束」而不是「中斷」：Windows 上即使 stdin 是管線
                // 也照樣讀得到（已實測：退出碼 0、檔案有完整 ftyp）。這一點是整條優雅收尾的
                // 前提——送不進去就只能硬砍，檔案就留在 .tmp。
                _proc.StandardInput.WriteLine("q");
                _proc.StandardInput.Flush();
            }
            catch (ObjectDisposedException)
            {
                // 行程物件已被釋放（ObjectDisposedException 是 InvalidOperationException 的子類，
                // 必須排在它前面才攔得到）。
            }
            catch (InvalidOperationException)
            {
                // 未重導 stdin：沒有溫和的收尾方式，交給 Kill。
            }
            catch (IOException)
            {
                // 管線已關（行程正在結束）。
            }
        }

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