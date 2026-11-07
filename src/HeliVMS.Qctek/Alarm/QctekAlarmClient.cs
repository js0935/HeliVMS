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

    /// <summary>連線識別（<c>ALARM_HELLO</c> 的第一個整數；QCV8001 為每條連線的計數）。</summary>
    public int Id { get; init; } = 1;

    /// <summary>裝置資訊字串（<c>ALARM_HELLO %d =%s</c> 的 <c>%s</c>）。</summary>
    public required string Info { get; init; }

    /// <summary>保活間隔。</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>斷線後的重連延遲。</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>可選的訊息記錄。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// NVR → CMS 警報回報客戶端：連上 CMS 的 Alarm Service、送 <c>ALARM_HELLO</c>、
/// 週期性 <c>ALARM_KEEP_ALIVE</c>、斷線自動重連（並重送 HELLO）。
/// </summary>
/// <remarks>
/// <para>
/// 方向與格式由反編譯確認：CMS 端 <c>QC_RM_Alarm</c> 監聽 AlarmServiceIP:Port
/// （<c>cms_alarm.cpp:0x241</c>「alarm server start failed!」）；NVR 端 <c>QC_Alarm</c>
/// 的 <c>alarm_to_cms</c> 送 <c>"ALARM_HELLO %d =%s"</c> 並讀回 CMS 的回應
/// （NVR 側 trace <c>{CMS} HelloPacketR</c>）。
/// </para>
/// <para>
/// <b>框格式採 NEMO 框</b>（16-byte 標頭＋payload，得 <see cref="NemoMessage"/>）：
/// QC_Alarm 是把訊息交給 <c>QC_Client::Write</c>（NEMO 傳輸層，其 Connect377／
/// CheckRespTimeout 等皆為 NEMO 家族）送出；精簡客戶端 NVDLL 則是自行組
/// <c>"NEMO %d "</c> 標頭後再送——兩邊一致。此推論標記為<b>待動態驗證</b>
/// （若實測不符，只需改 <see cref="SendAsync"/> 一處）。
/// </para>
/// <para>
/// 事件訊息（<c>ALARM_MSG</c>／<c>ALARM_VIDEO_LOSS</c> 等）的欄位順序尚未完全確定，
/// 本類別刻意不送——送錯格式會讓 CMS 寫入錯的事件時間（寧缺勿錯）。
/// </para>
/// </remarks>
public sealed class QctekAlarmClient : IAsyncDisposable
{
    private readonly QctekAlarmOptions _options;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public QctekAlarmClient(QctekAlarmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CmsHost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.CmsPort);
        _options = options;
    }

    public bool IsRunning => _loop is not null;

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
            catch (OperationCanceledException)
            {
                break;
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
        _options.Log?.Invoke($"QCTek alarm 已連線 {_options.CmsHost}:{_options.CmsPort}");

        using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reader = Task.Run(() => ReadLoopAsync(stream, link.Token), CancellationToken.None);

        try
        {
            // 連上後第一則：HELLO（CMS 會回 HelloPacketR，內容未還原——讀掉即可）。
            await SendAsync(stream, QctekAlarmMessages.Hello(_options.Id, _options.Info), link.Token)
                .ConfigureAwait(false);

            while (!link.IsCancellationRequested)
            {
                await Task.Delay(_options.KeepAliveInterval, link.Token).ConfigureAwait(false);
                await SendAsync(stream, QctekAlarmMessages.KeepAlive(_options.Id, _options.Info), link.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // 連線斷了（CMS 重啟、網路中斷）：往外層重連，讀取迴圈會自行結束。
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
