using HeliVMS.Shared;

namespace HeliVMS.App.Tests;

/// <summary>
/// 關機收尾的兩個行為契約：收尾步驟不能在呼叫端的同步內容上跑，而且整段流程一定要有上限。
///
/// <para>
/// 現場症狀是「關掉視窗之後 HeliVMS.exe 還在工作管理員裡」。原因不是誰忘了關東西，而是
/// 收尾工作在 UI 執行緒上被同步等待：那些工作內部都是 <c>await</c>，續體要排回同一個
/// Dispatcher，而 Dispatcher 正卡在 <c>Wait()</c> 裡——於是永遠等不到。行程不結束，
/// <c>index.db</c> 也就沒有乾淨關閉。
/// </para>
///
/// <para>
/// 這裡用一個「Post 之後什麼都不做」的 <see cref="SynchronizationContext"/> 模擬那個卡住的
/// Dispatcher：只要收尾步驟被丟回呼叫端執行，這個測試就會看到它。
/// </para>
/// </summary>
public sealed class ShutdownSequencerTests
{
    /// <summary>模擬「UI 執行緒正卡在同步等待」：Post 進來只計數，永不執行。</summary>
    private sealed class BlockingSynchronizationContext : SynchronizationContext
    {
        public int Posts;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref Posts);
        }
    }

    [Fact]
    public void 收尾步驟不會在呼叫端的同步內容上執行()
    {
        var previous = SynchronizationContext.Current;
        var blocked = new BlockingSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(blocked);
        try
        {
            SynchronizationContext? inside = null;
            var completed = ShutdownSequencer.Run(
                "停止錄影",
                () =>
                {
                    inside = SynchronizationContext.Current;
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(5),
                _ => { });

            Assert.True(completed);
            Assert.Null(inside);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void 收尾步驟逾時會回報並寫下是哪一步()
    {
        using var gate = new ManualResetEventSlim(false);
        var messages = new List<string>();

        var completed = ShutdownSequencer.Run(
            "停止錄影",
            () =>
            {
                gate.Wait(TimeSpan.FromSeconds(30));
                return Task.CompletedTask;
            },
            TimeSpan.FromMilliseconds(100),
            message =>
            {
                lock (messages)
                {
                    messages.Add(message);
                }
            });

        Assert.False(completed);
        Assert.Contains(messages, m => m.Contains("停止錄影") && m.Contains("逾時"));
        gate.Set();
    }

    [Fact]
    public void 收尾步驟拋例外會被記下來而不是讓關機崩掉()
    {
        var messages = new List<string>();

        var completed = ShutdownSequencer.Run(
            "關閉資料庫",
            () => throw new InvalidOperationException("資料庫被鎖住"),
            TimeSpan.FromSeconds(5),
            message =>
            {
                lock (messages)
                {
                    messages.Add(message);
                }
            });

        // 例外不是逾時：步驟確實「回來了」，關機流程要繼續往下收拾其他資源。
        Assert.True(completed);
        Assert.Contains(messages, m => m.Contains("關閉資料庫") && m.Contains("資料庫被鎖住"));
    }

    [Fact]
    public void 看門狗時間到只會觸發一次()
    {
        var fired = 0;
        using var firedOnce = new ManualResetEventSlim(false);
        using var watchdog = new ShutdownWatchdog(
            TimeSpan.FromMilliseconds(50),
            () =>
            {
                Interlocked.Increment(ref fired);
                firedOnce.Set();
            });

        Assert.True(firedOnce.Wait(TimeSpan.FromSeconds(10)), "看門狗沒有在期限內觸發");

        // 再等一下，確認不會重複觸發（重複執行結束行程的動作會讓日誌與清理各做兩次）。
        Thread.Sleep(200);
        Assert.True(watchdog.HasFired);
        Assert.Equal(1, Volatile.Read(ref fired));
    }

    [Fact]
    public void 看門狗在收尾完成後解除就不會再觸發()
    {
        var fired = 0;
        var watchdog = new ShutdownWatchdog(
            TimeSpan.FromMilliseconds(50),
            () => Interlocked.Increment(ref fired));

        watchdog.Dispose();
        Thread.Sleep(300);

        Assert.False(watchdog.HasFired);
        Assert.Equal(0, Volatile.Read(ref fired));
    }
}
