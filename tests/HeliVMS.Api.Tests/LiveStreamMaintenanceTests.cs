using HeliVMS.WebApi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeliVMS.Api.Tests;

/// <summary>
/// <see cref="LiveStreamMaintenance"/> 的定期維護迴圈。它每 10 秒回收逾時觀看會話並關閉
/// 閒置 publisher；真正要守的保證只有一條：某一次維護丟出例外時，迴圈必須活下來並在下一輪重試。
/// 迴圈若死掉不會有任何例外傳到外面，只會讓逾時會話與閒置 publisher 永遠佔住配額。
/// </summary>
public sealed class LiveStreamMaintenanceTests
{
    [Fact]
    public async Task 維護丟出例外時回報繼續且下一輪仍會執行()
    {
        var calls = 0;
        var maintenance = new LiveStreamMaintenance(
            _ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new InvalidOperationException("publisher 罷工");
                }

                return Task.FromResult(0);
            },
            NullLogger<LiveStreamMaintenance>.Instance);

        Assert.True(await maintenance.MaintainOnceAsync(CancellationToken.None));
        Assert.True(await maintenance.MaintainOnceAsync(CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 維護被取消時回報停止迴圈()
    {
        var maintenance = new LiveStreamMaintenance(
            token => Task.FromCanceled<int>(token),
            NullLogger<LiveStreamMaintenance>.Instance);

        Assert.False(await maintenance.MaintainOnceAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task 回收逾時會話時會記錄資訊()
    {
        var logger = new CapturingLogger<LiveStreamMaintenance>();
        var maintenance = new LiveStreamMaintenance(
            _ => Task.FromResult(3),
            logger);

        Assert.True(await maintenance.MaintainOnceAsync(CancellationToken.None));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task 維護丟出例外時會記錄錯誤()
    {
        var logger = new CapturingLogger<LiveStreamMaintenance>();
        var maintenance = new LiveStreamMaintenance(
            _ => throw new InvalidOperationException("boom"),
            logger);

        Assert.True(await maintenance.MaintainOnceAsync(CancellationToken.None));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
