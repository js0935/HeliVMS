using System.Threading.Channels;
using System.Windows.Threading;
using HeliVMS.Shared;

namespace HeliVMS.App.Services;

/// <summary>
/// 把 <see cref="AlertBroadcastHub"/> 的推播轉成 WPF 執行緒上的刷新回呼。
/// 警報視窗原本每 15 秒輪詢一次，新警報最慢要 15 秒才出現；接上推播後即時可見，
/// 輪詢退回為漏接時的保險。同一批多筆（例如一次寫入多個事件）只排一次刷新。
/// </summary>
public sealed class AlertFeed : IDisposable
{
    private readonly IDisposable _lease;
    private readonly Dispatcher _dispatcher;
    private readonly Action _onChange;
    private int _pending;

    public AlertFeed(AlertBroadcastHub hub, Dispatcher dispatcher, Action onChange)
    {
        _dispatcher = dispatcher;
        _onChange = onChange;
        _lease = hub.Subscribe(out var reader);
        _ = PumpAsync(reader);
    }

    private async Task PumpAsync(ChannelReader<AlertUpdate> reader)
    {
        await foreach (var _update in reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Interlocked.Exchange(ref _pending, 1) == 1)
            {
                continue;
            }

            _ = _dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                _onChange();
            });
        }
    }

    public void Dispose() => _lease.Dispose();
}
