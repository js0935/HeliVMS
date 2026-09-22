using System.ComponentModel;
using System.Diagnostics;

namespace HeliVMS.Storage;

/// <summary>補抓拉流來源解析（M96，§14.7 #10）：job→（設備 SD HTTP 串流位址＋落盤路徑）。</summary>
public sealed record EdgePullTarget(string SourceUri, string DestinationPath);

/// <summary>把外部進程當作純函式執行的抽象（M96）：測試注入 fake，無需真實 ffmpeg。</summary>
public delegate (int ExitCode, string StdOut, string StdErr) EdgeProcessExecutor(
    string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);

/// <summary>真實 ffmpeg 補抓 runner（M96）：`EdgeBackfillCommandFactory` 產　　指令→外部進程 pull 回主庫。</summary>
public sealed class EdgeFfmpegBackfillRunner : IEdgeBackfillRunner
{
    private readonly Func<EdgeBackfillJob, EdgePullTarget> _resolver;
    private readonly string _ffmpeg;
    private readonly EdgeProcessExecutor _execute;
    private readonly TimeSpan _timeout;

    public EdgeFfmpegBackfillRunner(
        Func<EdgeBackfillJob, EdgePullTarget> resolver,
        string? ffmpegPath = null,
        EdgeProcessExecutor? execute = null,
        TimeSpan? timeout = null)
    {
        _resolver = resolver;
        _ffmpeg = ffmpegPath ?? "ffmpeg";
        _execute = execute ?? RunProcess;
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
    }

    public bool IsToolAvailable()
    {
        try
        {
            return _execute(_ffmpeg, ["-version"], TimeSpan.FromSeconds(10), CancellationToken.None).ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask<EdgeBackfillResult> RunAsync(EdgeBackfillJob job, CancellationToken ct)
    {
        if (!IsToolAvailable())
        {
            return new EdgeBackfillResult(false, $"ffmpeg 不可用（{_ffmpeg}）");
        }

        var target = _resolver(job);
        try
        {
            var args = EdgeBackfillCommandFactory.BuildArguments(target.SourceUri, target.DestinationPath, job.StartUtc, job.EndUtc);
            var result = await Task.Run(() => _execute(_ffmpeg, args, _timeout, ct), ct);
            return result.ExitCode == 0
                ? new EdgeBackfillResult(true)
                : new EdgeBackfillResult(false, $"exit={result.ExitCode}: {Tail(result.StdErr)}");
        }
        catch (Exception ex)
        {
            return new EdgeBackfillResult(false, ex.Message);
        }
    }

    private static string Tail(string text)
    {
        if (text.Length > 500)
        {
            return text[^500..];
        }

        return text;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("無法啟動外部進程");
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            waitCts.Cancel();
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Win32Exception)
            {
                // 進程可能已結束
            }

            throw new TimeoutException($"外部進程逾時（>{timeout.TotalSeconds:0}s）");
        }

        var exit = proc.ExitCode;
        var err = string.Empty;
        try
        {
            err = stderr.Result;
        }
        catch
        {
            // 逾時取消後無 stderr
        }

        return (exit, stdout.Result, err);
    }
}