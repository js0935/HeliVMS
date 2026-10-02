using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// 會話帳本的測試（R244）。
/// <para>
/// 這裡保護的是<b>資源配額</b>與<b>連線生命週期</b>。若配額失效，操作員就能用一顆
/// 分頁吃掉一整台主機的 CPU；若逾時回收失效，被遺忘的會話會把配額永久佔死，
/// 直到沒有人能再看任何攝影機。
/// </para>
/// </summary>
public class WhepSessionStoreTests
{
    private sealed class FakePeer : IWhepPeer
    {
        public bool Disposed { get; private set; }
        public bool Closed { get; private set; }
        public int Forwarded { get; private set; }

        public Task<WhepAnswer> NegotiateAsync(string offerSdp, CancellationToken token)
            => Task.FromResult(new WhepAnswer("v=0"));

        public void Forward(RtpHeader header, ReadOnlySpan<byte> payload) => Forwarded++;

        public void Close(string reason) => Closed = true;

        public void Dispose() => Disposed = true;
    }

    private static WhepSession Session(string id, int channel, long ticks)
        => new(id, channel, new FakePeer(), ticks);

    [Fact]
    public void 新增後可在通道與ID兩種方式查到()
    {
        var store = new WhepSessionStore();
        var session = Session("a", 7, 0);

        store.Add(session, 8);

        Assert.Same(session, store.Find("a"));
        Assert.Equal(1, store.Count);
        Assert.Equal(1, store.CountFor(7));
        Assert.Single(store.ListFor(7));
    }

    [Fact]
    public void 移除後通道計數歸零且不再查到()
    {
        var store = new WhepSessionStore();
        store.Add(Session("a", 7, 0), 8);

        Assert.True(store.Remove("a", out var removed));
        Assert.NotNull(removed);

        Assert.Null(store.Find("a"));
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.CountFor(7));
    }

    [Fact]
    public void 移除不存在的會話回傳失敗()
    {
        var store = new WhepSessionStore();

        Assert.False(store.Remove("nope", out _));
        Assert.False(store.Remove(null, out _));
        Assert.False(store.Remove("", out _));
    }

    [Fact]
    public void 超過上限時丟出限制例外且不留下任何痕跡()
    {
        // 這是最重要的資安斷言：被拒絕的觀看者不能佔到任何名額。
        var store = new WhepSessionStore();
        store.Add(Session("a", 7, 0), 1);

        var ex = Assert.Throws<WhepViewerLimitException>(() => store.Add(Session("b", 7, 0), 1));

        Assert.Equal(7, ex.ChannelId);
        Assert.Equal(1, ex.Limit);
        Assert.Equal(1, store.Count);
        Assert.Equal(1, store.CountFor(7));
        Assert.Null(store.Find("b"));
    }

    [Fact]
    public void 配額只作用在該通道不影響其他通道()
    {
        var store = new WhepSessionStore();
        store.Add(Session("a", 7, 0), 1);

        // 通道 8 應該完全不受通道 7 已滿的影響。
        store.Add(Session("b", 8, 0), 1);

        Assert.Equal(1, store.CountFor(7));
        Assert.Equal(1, store.CountFor(8));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void 有人離開後名額會釋放()
    {
        var store = new WhepSessionStore();
        store.Add(Session("a", 7, 0), 1);
        Assert.Throws<WhepViewerLimitException>(() => store.Add(Session("b", 7, 0), 1));

        store.Remove("a", out _);

        store.Add(Session("b", 7, 0), 1);
        Assert.Equal(1, store.CountFor(7));
    }

    [Fact]
    public void 重複的會話ID被拒絕()
    {
        var store = new WhepSessionStore();
        store.Add(Session("same", 7, 0), 8);

        Assert.Throws<InvalidOperationException>(() => store.Add(Session("same", 7, 0), 8));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void 上限必須至少為一()
    {
        var store = new WhepSessionStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Add(Session("a", 7, 0), 0));
    }

    [Fact]
    public void 逾時的會話被回收()
    {
        var now = 1000L;
        var store = new WhepSessionStore(() => now);
        var stale = new WhepSession("stale", 7, new FakePeer(), 0);
        store.Add(stale, 8);

        now = TimeSpan.FromMinutes(1).Ticks;

        var swept = store.SweepIdle(TimeSpan.FromSeconds(30));

        Assert.Single(swept);
        Assert.Same(stale, swept[0]);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.CountFor(7));
    }

    [Fact]
    public void 剛有活動的會話不會被回收()
    {
        // 這是「逾時」與「使用者還在看」的分界線；弄錯會讓人在看的畫面被砍掉。
        var now = TimeSpan.FromMinutes(1).Ticks;
        var store = new WhepSessionStore(() => now);
        var live = Session("live", 7, 0);
        store.Add(live, 8);

        live.Touch(now);
        var swept = store.SweepIdle(TimeSpan.FromSeconds(30));

        Assert.Empty(swept);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void 回收一個逾時會話不影響尚未逾時的會話()
    {
        var now = TimeSpan.FromMinutes(2).Ticks;
        var store = new WhepSessionStore(() => now);
        store.Add(Session("old", 7, 0), 8);
        store.Add(Session("new", 7, TimeSpan.FromMinutes(2).Ticks), 8);

        var swept = store.SweepIdle(TimeSpan.FromSeconds(30));

        Assert.Single(swept);
        Assert.Equal("old", swept[0].Id);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void 清空全部會話()
    {
        var store = new WhepSessionStore();
        store.Add(Session("a", 7, 0), 8);
        store.Add(Session("b", 8, 0), 8);

        Assert.Equal(2, store.DrainAll().Count);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.CountFor(7));
        Assert.Equal(0, store.CountFor(8));
    }

    [Fact]
    public async Task 併發新增不會突破上限()
    {
        // TOCTOU 防護：若「先查再加入」不在同一把鎖內，這個測試會有時失敗。
        var store = new WhepSessionStore();
        const int limit = 8;
        const int attempts = 64;

        var accepted = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, attempts), async (i, ct) =>
        {
            try
            {
                store.Add(new WhepSession($"s{i}", 7, new FakePeer(), 0), limit);
                Interlocked.Increment(ref accepted);
            }
            catch (WhepViewerLimitException)
            {
                // 預期：超過配額。
            }

            await Task.CompletedTask;
        });

        Assert.Equal(limit, accepted);
        Assert.Equal(limit, store.CountFor(7));
    }

    [Fact]
    public async Task 併發查找與移除不會擲出例外()
    {
        var store = new WhepSessionStore();
        for (var i = 0; i < 16; i++)
        {
            store.Add(new WhepSession($"s{i}", i % 4, new FakePeer(), 0), 16);
        }

        await Parallel.ForEachAsync(Enumerable.Range(0, 200), async (i, ct) =>
        {
            if (i % 3 == 0)
            {
                store.Remove($"s{i % 16}", out _);
            }
            else if (i % 3 == 1)
            {
                store.Find($"s{i % 16}");
            }
            else
            {
                store.SweepIdle(TimeSpan.FromHours(1));
            }

            await Task.CompletedTask;
        });

        Assert.InRange(store.Count, 0, 16);
    }
}