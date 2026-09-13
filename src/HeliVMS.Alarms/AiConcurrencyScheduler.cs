using System;
using System.Threading;
using System.Threading.Tasks;

namespace HeliVMS.Alarms;

/// <summary>
/// AI 推理全域限併發佇列：多格同時推送時，最多 <c>maxConcurrency</c> 
/// 同時執行推理，其餘佇列等待；支援等待逾時略過（當資源繁忙時保留最新幀）。
/// </summary>
public sealed class AiConcurrencyScheduler : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly int _maxConcurrency;

    /// <summary>全域共享實例（預設 maxConcurrency = 2，可覆寫 HELIVMS_AI_CONCURRENCY）。</summary>
    public static AiConcurrencyScheduler Shared { get; } = CreateShared();

    /// <summary>產生含上限之排程器（測試可自訂）。</summary>
    public AiConcurrencyScheduler(int maxConcurrency)
    {
        _maxConcurrency = Math.Clamp(maxConcurrency, 1, 16);
        _gate = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
    }

    public int MaxConcurrency => _maxConcurrency;

    public int ActiveCount => _maxConcurrency - _gate.CurrentCount;

    /// <summary>執行推理工作（阻塞直到取得併發配額）。</summary>
    public void RunSync(Action work)
    {
        _gate.Wait();
        try
        {
            work();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>嘗試立即取得配額（false = 並發已滿，跳過此幀推理）。</summary>
    public bool TryRun(Action work)
    {
        if (!_gate.Wait(0))
        {
            return false;
        }

        try
        {
            work();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private static AiConcurrencyScheduler CreateShared()
    {
        var concurrency = 2;
        var raw = Environment.GetEnvironmentVariable("HELIVMS_AI_CONCURRENCY");
        if (!string.IsNullOrWhiteSpace(raw) &&
            int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var v) &&
            v >= 1)
        {
            concurrency = Math.Clamp(v, 1, 16);
        }

        return new AiConcurrencyScheduler(concurrency);
    }
}