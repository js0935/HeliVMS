using System.Collections.Concurrent;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 通知中心（M22，§18.5 通知平面）：佇列接收（非阻塞）＋定時批量分派。
/// 依 notify.* 設定選擇外送通道（Webhook／SMTP）；失敗指數退避重試
/// （backoffBase×attempt，最多 maxAttempts 次）。呼叫端一律 <see cref="Enqueue"/>，
/// 嚴禁在事件引擎鎖內同步發送。
/// </summary>
public sealed class NotificationService : IDisposable
{
    private sealed class Item
    {
        public required AlarmEventRecord Record { get; init; }
        public int Attempts;
        public DateTime NextDueUtc = DateTime.MinValue;
    }

    private readonly SettingsRepository _settings;
    private readonly WebhookNotifier _webhook = new();
    private readonly SmtpNotifier _smtp = new();
    private readonly NotificationLogRepository _log;
    private readonly ConcurrentQueue<Item> _queue = new();
    private readonly ConcurrentQueue<Item> _retry = new();
    private readonly System.Threading.Timer _timer;
    private readonly TimeSpan _backoffBase;
    private readonly int _maxAttempts;
    private int _processing;
    private bool _disposed;

    /// <summary>每次送達／失敗之活動（留痕與診斷；UI/E2E 可訂閱）。</summary>
    public event EventHandler<string>? Activity;

    public NotificationService(
        SqliteStore store,
        TimeSpan? interval = null,
        TimeSpan? backoffBase = null,
        int maxAttempts = 3)
    {
        _settings = new SettingsRepository(store);
        _log = new NotificationLogRepository(store);
        _backoffBase = backoffBase ?? TimeSpan.FromSeconds(1);
        _maxAttempts = maxAttempts;
        var tick = interval ?? TimeSpan.FromSeconds(2);
        _timer = new System.Threading.Timer(_ => TryProcess(), null, tick, tick);
    }

    public int PendingCount => _queue.Count + _retry.Count;

    public long DeliveredCount { get; private set; }

    public long FailedCount { get; private set; }

    /// <summary>靜默時段跳過（不送、不落 log）的事件筆數。</summary>
    public long SkippedDuringQuietCount { get; private set; }

    /// <summary>將事件放入佇列（非阻塞；前台呼叫安全）。</summary>
    public void Enqueue(AlarmEventRecord record)
    {
        if (_disposed)
        {
            return;
        }

        _queue.Enqueue(new Item { Record = record });
    }

    /// <summary>立即處理一輪（測試/交付前呼叫）。</summary>
    public void FlushNow() => TryProcess();

    private void TryProcess()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _processing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessBatchAsync();
            }
            catch (Exception)
            {
                // 單筆失敗已由 notifier 吞下；此處僅保護 worker
            }
            finally
            {
                Interlocked.Exchange(ref _processing, 0);
                if (!_disposed && (!_queue.IsEmpty || !_retry.IsEmpty))
                {
                    TryProcess();
                }
            }
        });
    }

    private async Task ProcessBatchAsync()
    {
        var cfg = NotificationSettings.Load(_settings);
        if (!cfg.Enabled || !cfg.AnyChannelConfigured)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(cfg.WebhookUrl) &&
            cfg.SmtpEnabled &&
            !string.IsNullOrWhiteSpace(cfg.SmtpHost))
        {
            await ProcessSmtpBatchAsync(cfg);
            return;
        }

        while (!_disposed && _queue.TryDequeue(out var item))
        {
            await ProcessItemAsync(cfg, item);
        }

        var notDue = new List<Item>();
        while (!_disposed && _retry.TryDequeue(out var item))
        {
            if (item.NextDueUtc <= DateTime.UtcNow)
            {
                await ProcessItemAsync(cfg, item);
            }
            else
            {
                notDue.Add(item);
            }
        }

        foreach (var item in notDue)
        {
            _retry.Enqueue(item);
        }
    }

    private async Task ProcessSmtpBatchAsync(NotificationSettings cfg)
    {
        var items = new List<Item>();
        while (!_disposed && _queue.TryDequeue(out var item))
        {
            items.Add(item);
        }

        var notDue = new List<Item>();
        while (!_disposed && _retry.TryDequeue(out var item))
        {
            if (item.NextDueUtc <= DateTime.UtcNow)
            {
                items.Add(item);
            }
            else
            {
                notDue.Add(item);
            }
        }

        foreach (var item in notDue)
        {
            _retry.Enqueue(item);
        }

        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            item.Attempts++;
        }

        if (cfg.IsInQuietHours(DateTime.Now))
        {
            foreach (var item in items)
            {
                SkippedDuringQuietCount++;
                Activity?.Invoke(this,
                    $"靜默時段，跳過通知（{item.Record.EventType} 頻道{item.Record.ChannelId}）。");
            }

            return;
        }

        var batchOk = await _smtp.SendAsync(cfg, items.Select(i => i.Record).ToList());
        if (batchOk)
        {
            DeliveredCount += items.Count;
            foreach (var item in items)
            {
                _log.Add(item.Record.ChannelId, item.Record.EventType, "smtp", true, item.Attempts, null);
            }

            Activity?.Invoke(this, $"已通知（{items.Count} 則，SMTP 合併）。");
            return;
        }

        foreach (var item in items)
        {
            if (item.Attempts >= _maxAttempts)
            {
                FailedCount++;
                _log.Add(item.Record.ChannelId, item.Record.EventType, "smtp", false, item.Attempts, "smtp 失敗");
                Activity?.Invoke(this,
                    $"通知失敗（{item.Record.EventType} 頻道{item.Record.ChannelId}，重試 {item.Attempts} 次）。");
            }
            else
            {
                item.NextDueUtc = DateTime.UtcNow.Add(_backoffBase * item.Attempts);
                _retry.Enqueue(item);
            }
        }
    }

    private async Task ProcessItemAsync(NotificationSettings cfg, Item item)
    {
        item.Attempts++;

        if (cfg.IsInQuietHours(DateTime.Now))
        {
            SkippedDuringQuietCount++;
            Activity?.Invoke(this, $"靜默時段，跳過通知（{item.Record.EventType} 頻道{item.Record.ChannelId}）。");
            return;
        }

        var success = true;
        var routes = new List<string>();
        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(cfg.WebhookUrl))
        {
            routes.Add("webhook");
            var wOk = await _webhook.SendAsync(cfg.WebhookUrl, item.Record);
            success &= wOk;
            if (!wOk)
            {
                failures.Add("webhook");
            }
        }

        if (cfg.SmtpEnabled && !string.IsNullOrWhiteSpace(cfg.SmtpHost))
        {
            routes.Add("smtp");
            var sOk = await _smtp.SendAsync(cfg, item.Record);
            success &= sOk;
            if (!sOk)
            {
                failures.Add("smtp");
            }
        }

        var route = string.Join("+", routes);
        if (success)
        {
            DeliveredCount++;
            _log.Add(item.Record.ChannelId, item.Record.EventType, route, true, item.Attempts, null);
            Activity?.Invoke(this, $"已通知（{item.Record.EventType} 頻道{item.Record.ChannelId}）。");
        }
        else if (item.Attempts >= _maxAttempts)
        {
            FailedCount++;
            var reason = $"{string.Join("、", failures)} 失敗";
            _log.Add(item.Record.ChannelId, item.Record.EventType, route, false, item.Attempts, reason);
            Activity?.Invoke(this,
                $"通知失敗（{item.Record.EventType} 頻道{item.Record.ChannelId}，重試 {item.Attempts} 次）。");
        }
        else
        {
            item.NextDueUtc = DateTime.UtcNow.Add(_backoffBase * item.Attempts);
            _retry.Enqueue(item);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }
}