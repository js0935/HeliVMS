using System.Collections.Concurrent;
using System.Net.Sockets;
using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Alarm;

/// <summary>QCTek CMS 警報回報的連線設定。</summary>
public sealed record QctekAlarmOptions
{
    /// <summary>CMS 的 Alarm Service 主機（CMS 端設定頁的 AlarmServiceIP）。</summary>
    public required string CmsHost { get; init; }

    /// <summary>CMS 的 Alarm Service 埠（CMS 端設定頁的 AlarmServicePort）。</summary>
    public required int CmsPort { get; init; }

    /// <summary>裝置資訊字串（<c>ALARM_HELLO %d =%s</c> 的 <c>%s</c>；事件訊息結尾的 <c>=%s</c> 用同一個）。</summary>
    public required string Info { get; init; }

    /// <summary>
    /// HELLO 重送間隔。QCV8001 的連線主控迴圈每 5 輪重送一次 HELLO 當保活
    /// （每 61 輪檢查「期間有沒有送出任何訊息」，沒有就移除連線）——週期 HELLO
    /// 同時滿足兩個條件；發送端沒有 ALARM_KEEP_ALIVE。
    /// </summary>
    public TimeSpan HelloInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>斷線後的重連延遲。</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>未送出事件的佇列上限；滿了丟最舊的（QCV8001 的待送槽同樣是滿了就丟）。</summary>
    public int QueueCapacity { get; init; } = 256;

    /// <summary>可選的訊息記錄。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// NVR → CMS 警報回報客戶端：連上 CMS 的 Alarm Service、送 <c>ALARM_HELLO</c>、
/// 週期重送 HELLO 當保活、事件以 <c>ALARM_MSG</c> 即時送出、斷線自動重連。
/// </summary>
/// <remarks>
/// <para>
/// 方向與格式由反編譯確認：CMS 端 <c>QC_RM_Alarm</c> 監聽 AlarmServiceIP:Port
/// （<c>cms_alarm.cpp:0x241</c>「alarm server start failed!」）；NVR 端 <c>QC_Alarm</c>
/// 的 <c>alarm_to_cms</c> 送 <c>"ALARM_HELLO %d =%s"</c> 並讀回 CMS 的回應
/// （NVR 側 trace <c>{CMS} HelloPacketR</c>）。HELLO 的 <c>%d</c> 與 ALARM_MSG 的第一欄
/// 是同一個「連線識別」——CMS 以它找回連線格。
/// </para>
/// <para>
/// <b>框格式採 NEMO 框</b>（16-byte 標頭＋payload，得 <see cref="NemoMessage"/>）：
/// QC_Alarm 是把訊息交給 <c>QC_Client::Write</c>（NEMO 傳輸層）送出——此推論標記為
/// <b>待動態驗證</b>（若實測不符，只需改 <see cref="SendAsync"/> 一處）。
/// </para>
/// <para>
/// 事件佇列只在「連線中」被抽乾：斷線期間的事件留在佇列裡（上限
/// <see cref="QctekAlarmOptions.QueueCapacity"/>，滿了丟最舊），重連後補送；
/// 送出途中斷線的那一筆會遺失——它已經離隊，重送會與 CMS 端已收到的重複。
/// </para>
/// </remarks>
public sealed class QctekAlarmClient : IAsyncDisposable
{
    private readonly QctekAlarmOptions _options;
    private readonly ConcurrentQueue<QctekAlarmEvent> _pending = new();
    private readonly SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _connectionCounter;
    private bool _disposed;

    public QctekAlarmClient(QctekAlarmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CmsHost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.CmsPort);
        ArgumentNullException.ThrowIfNull(options.Info);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.QueueCapacity);
        _options = options;
    }

    public bool IsRunning => _loop is not null;

    /// <summary>目前佇列中等待送出的事件數（診斷用）。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>啟動背景回報迴圈（非阻塞）。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// 排入一筆事件（任何執行緒可呼叫）。連線中會立刻被送出；斷線期間排隊等重連。
    /// </summary>
    public void Report(QctekAlarmEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_disposed)
        {
            return;
        }

        _pending.Enqueue(e);
        while (_pending.Count > _options.QueueCapacity)
        {
            _pending.TryDequeue(out _); // 滿了丟最舊——陳舊事件的價值最低
        }

        _wake.Release();
    }

    /// <summary>停止回報並等待背景迴圈結束。</summary>
    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break; // 真正的停止
            }
            catch (OperationCanceledException)
            {
                // 對端關閉連線（讀取迴圈結束時取消連結權杖以喚醒寫入迴圈）：
                // 這是正常的重連路徑，不是錯誤——不能當成「停止」而停掉整個回報。
            }
            catch (Exception ex)
            {
                _options.Log?.Invoke($"QCTek alarm 連線中斷：{ex.Message}");
            }

            try
            {
                await Task.Delay(_options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunConnectedAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(_options.CmsHost, _options.CmsPort, cancellationToken).ConfigureAwait(false);
        client.NoDelay = true;
        var stream = client.GetStream();

        // 連線識別：QCV8001 是每條連線的計數。HELLO 與該連線上的所有 ALARM_MSG
        // 必須用同一個值——CMS 用它把後續訊息綁回這條連線。
        var connectionId = Interlocked.Increment(ref _connectionCounter);
        _options.Log?.Invoke($"QCTek alarm 已連線 {_options.CmsHost}:{_options.CmsPort}（id {connectionId}）");

        using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 讀取結束（對端關閉、I/O 錯誤）代表這條連線死了：取消連結權杖把寫入迴圈
        // 從「等 HELLO 到期」或「等事件」中叫醒，否則它會傻等到下一次週期才發現斷線
        // ——症狀是 CMS 重啟後好幾秒到幾十秒內事件全部塞在佇列裡。
        var reader = Task.Run(() =>
        {
            try
            {
                ReadLoopAsync(stream, link.Token).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 讀取的任何結束都走斷線路徑。
            }
            finally
            {
                try
                {
                    link.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 方法已收尾（正常停止）：沒有東西要喚醒。
                }
            }
        }, CancellationToken.None);

        try
        {
            // 連上後第一則：HELLO（CMS 會回 HelloPacketR，內容未還原——讀掉即可）。
            await SendAsync(stream, QctekAlarmMessages.Hello(connectionId, _options.Info), link.Token)
                .ConfigureAwait(false);
            var nextHello = DateTime.UtcNow + _options.HelloInterval;

            while (!link.IsCancellationRequested)
            {
                // 睡到「有事件」或「HELLO 到期」——兩者取先到者。
                var delay = nextHello - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await _wake.WaitAsync(delay, link.Token).ConfigureAwait(false);
                }

                // 事件優先（即時性），再把佇列抽乾。
                while (_pending.TryDequeue(out var e))
                {
                    await SendAsync(
                        stream,
                        QctekAlarmMessages.Msg(
                            connectionId, e.CameraNumber, e.LocalTime, e.Name, e.Describe,
                            e.Priority, _options.Info, e.EventType),
                        link.Token).ConfigureAwait(false);
                }

                if (DateTime.UtcNow >= nextHello)
                {
                    // 保活：重送 HELLO（QCV8001 的連線主控迴圈行為——HELLO 同時是「活著」的證明）。
                    await SendAsync(stream, QctekAlarmMessages.Hello(connectionId, _options.Info), link.Token)
                        .ConfigureAwait(false);
                    nextHello = DateTime.UtcNow + _options.HelloInterval;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // 連線斷了（CMS 重啟、網路中斷）：往外層重連；佇列中的事件會在下一次連線補送。
        }
        finally
        {
            link.Cancel();
            try
            {
                await reader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task ReadLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            int count;
            try
            {
                count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                return; // 斷線：由寫入端觸發重連
            }

            if (count == 0)
            {
                return; // CMS 關閉連線
            }

            // 回應目前沒有語意（HelloPacketR 內容未還原）：讀掉即可。
        }
    }

    private static Task SendAsync(NetworkStream stream, string message, CancellationToken cancellationToken)
        => stream.WriteAsync(NemoMessage.EncodeText(message).AsMemory(), cancellationToken).AsTask();
}
