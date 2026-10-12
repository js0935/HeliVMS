using HeliVMS.Shared;

namespace HeliVMS.Recording.Talk;

/// <summary>
/// 一次進行中的對講。音訊播完 ffmpeg 會自然結束；呼叫端也可直接處置以立即中斷。
/// </summary>
public sealed class TalkSession : IAsyncDisposable
{
    private readonly IProcess _process;
    private readonly FfmpegProcessRegistry.FfmpegJob _job;
    private bool _disposed;

    internal TalkSession(IProcess process, FfmpegProcessRegistry.FfmpegJob job, TalkTarget target)
    {
        _process = process;
        _job = job;
        Target = target;
    }

    /// <summary>此次對講的目標（位址、傳輸方式、供應商）。</summary>
    public TalkTarget Target { get; }

    /// <summary>底層 ffmpeg 行程是否已結束。</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>
    /// 等候對講播放結束並回傳 ffmpeg 結束碼；非零結束會記為失敗（附 stderr）。
    /// 取消時會終止行程並把該次對講記為失敗，再將 <see cref="OperationCanceledException"/> 往外丟。
    /// </summary>
    public async Task<int> WaitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var exitCode = _process.ExitCode;
            if (exitCode != 0)
            {
                _job.Fail(await ReadErrorAsync(cancellationToken).ConfigureAwait(false));
            }

            return exitCode;
        }
        catch (OperationCanceledException)
        {
            _process.Kill();
            _job.Fail("對講已取消");
            throw;
        }
        finally
        {
            _disposed = true;
            _job.Dispose();
            await _process.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Kill();
        _job.Dispose();
        await _process.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<string?> ReadErrorAsync(CancellationToken cancellationToken)
    {
        var text = await _process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
