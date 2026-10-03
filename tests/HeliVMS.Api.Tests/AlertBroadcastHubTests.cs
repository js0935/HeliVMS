using System.Threading.Channels;
using HeliVMS.Shared;

namespace HeliVMS.Api.Tests;

/// <summary>
/// <see cref="AlertBroadcastHub"/> 是 /api/alerts/ws 的扇出中樞。它的錯法都是「靜默漏送」：
/// 發佈時快照漏掉一個訂閱者、取消訂閱後仍寫入已完成的通道、重複釋放誤關別的通道。
/// 這些都不會拋例外，只會讓某個操作員的看板停止更新。
/// </summary>
public sealed class AlertBroadcastHubTests
{
    [Fact]
    public void 發佈的更新會送到訂閱者()
    {
        var hub = new AlertBroadcastHub();
        using var lease = hub.Subscribe(out var reader);

        hub.Publish(new AlertUpdate("ack", 42, "acknowledged"));

        Assert.True(reader.TryRead(out var update));
        Assert.Equal(new AlertUpdate("ack", 42, "acknowledged"), update);
    }

    [Fact]
    public void 多個訂閱者都會收到同一則更新()
    {
        var hub = new AlertBroadcastHub();
        using var a = hub.Subscribe(out var readerA);
        using var b = hub.Subscribe(out var readerB);

        hub.Publish(new AlertUpdate("disposition", 7, "closed"));

        Assert.True(readerA.TryRead(out var first));
        Assert.True(readerB.TryRead(out var second));
        Assert.Equal(first, second);
        Assert.Equal(7, first.EventId);
    }

    [Fact]
    public async Task 取消訂閱後不再收到更新且通道完成()
    {
        var hub = new AlertBroadcastHub();
        var lease = hub.Subscribe(out var reader);

        lease.Dispose();
        hub.Publish(new AlertUpdate("ack", 1));

        Assert.False(reader.TryRead(out _));
        await reader.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void 取消訂閱不影響其他訂閱者()
    {
        var hub = new AlertBroadcastHub();
        var leaving = hub.Subscribe(out _);
        using var staying = hub.Subscribe(out var reader);

        leaving.Dispose();
        hub.Publish(new AlertUpdate("ack", 5));

        Assert.True(reader.TryRead(out var update));
        Assert.Equal(5, update.EventId);
    }

    [Fact]
    public void 重複釋放訂閱不會影響其他訂閱者()
    {
        var hub = new AlertBroadcastHub();
        var lease = hub.Subscribe(out _);
        using var other = hub.Subscribe(out var reader);

        lease.Dispose();
        lease.Dispose();

        hub.Publish(new AlertUpdate("ack", 9));
        Assert.True(reader.TryRead(out var update));
        Assert.Equal(9, update.EventId);
    }

    [Fact]
    public void 沒有訂閱者時發佈不會丟例外()
    {
        var hub = new AlertBroadcastHub();
        hub.Publish(new AlertUpdate("ack", 1));
    }
}
