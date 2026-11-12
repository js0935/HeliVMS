using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// NEMO 伺服器：接受 QCTek CMS（QCC／舊 Center 系列）的 TCP 連線，
/// 依 16-byte 標頭分幀、交由 <see cref="QctekCommandHandler"/> 產生回應後寫回。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼是獨立 TCP 埠：QCV8001 把 NEMO 多工在 Web 埠（<c>HttpServer::PreParseRequest</c>
/// 分辨 NEMO／GET）；HeliVMS 的 Web 服務是 Kestrel，教它解析非 HTTP 串流代價高又易碎。
/// 獨立埠＋在 CMS 端把「裝置位址／埠」指向它，是同樣相容、維護成本最低的做法
/// （QCC 的裝置設定本來就允許指定埠）。
/// </para>
/// <para>
/// 容錯原則：單一連線的任何例外都不影響伺服器本體；未知命令不回覆（維持 <c>null</c> 語意）；
/// 串流不同步（標頭不是 NEMO）時直接關閉該連線——不要嘗試猜測重新同步點。
/// </para>
/// </remarks>
public sealed class NemoServer : IAsyncDisposable
{
    private readonly IPAddress _address;
    private readonly int _requestedPort;
    private readonly Action<string>? _log;
    private readonly IQctekDeviceContext _context;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Thread? _acceptThread;
    private readonly ConcurrentDictionary<Socket, byte> _connections = new();

    /// <param name="context">裝置資訊（版本、頻道數…），供 <see cref="QctekSession"/> 使用。</param>
    /// <param name="address">綁定位址；預設所有介面。</param>
    /// <param name="port">綁定埠；<c>0</c>＝由系統指派（測試用）。</param>
    /// <param name="log">可選的訊息記錄（連線進出、例外）。</param>
    public NemoServer(
        IQctekDeviceContext context,
        IPAddress? address = null,
        int port = 0,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        _address = address ?? IPAddress.Any;
        _requestedPort = port;
        _log = log;
        _context = context;
    }

    /// <summary>實際綁定的埠（<c>port: 0</c> 時由系統指派）。</summary>
    public int Port { get; private set; }

    /// <summary>開始接受連線（非阻塞）。</summary>
    /// <exception cref="InvalidOperationException">已啟動。</exception>
    public void Start()
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("NEMO 伺服器已啟動。");
        }

        var listener = new TcpListener(_address, _requestedPort);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener;
        _cts = new CancellationTokenSource();
        // accept 迴圈也用專用執行緒：新連線的「第一個回應」的時效是協定行為的一部分
        // （見 DedicatedThread）；accept 的喚醒排在執行緒池佇列裡一樣會吃到幾十毫秒。
        var cts = _cts;
        _acceptThread = new Thread(
            () => AcceptLoopAsync(listener, cts.Token).GetAwaiter().GetResult())
        {
            IsBackground = true,
            Name = "qctek-nemo-accept",
        };
        _acceptThread.Start();
        _log?.Invoke($"NEMO server listening on {_address}:{Port}");
    }

    /// <summary>停止接受連線並等待既有的接受迴圈結束。</summary>
    public async Task StopAsync()
    {
        if (_listener is null)
        {
            return;
        }

        _cts?.Cancel();
        _listener.Stop();
        _listener = null;

        // 同步迴圈阻塞在 socket 讀上，取消令牌打不斷它——關閉每條連線讓阻塞讀拋出。
        foreach (var socket in _connections.Keys)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
            }

            try
            {
                socket.Close();
            }
            catch (Exception)
            {
            }
        }

        if (_acceptThread is not null)
        {
            _acceptThread.Join(TimeSpan.FromSeconds(3));
            _acceptThread = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break; // StopAsync 關掉了 listener
            }
            catch (SocketException ex)
            {
                _log?.Invoke($"NEMO accept 失敗：{ex.SocketErrorCode}");
                break;
            }

            // 連線各自獨立；HandleConnection 內部吞掉所有例外。
            // 專用執行緒（非執行緒池）＋同步 I/O：回應時效是協定行為的一部分，見 DedicatedThread。
            DedicatedThread.Run(() => HandleConnection(client), "qctek-nemo-conn");
        }
    }

    private void HandleConnection(TcpClient client)
    {
        var socket = client.Client;
        _connections[socket] = 0;
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var pending = new NemoSessionLoop.FrameBuffer();
                var read = new byte[8192];
                var remote = SafeRemoteEndPoint(client);

                // 每個連線與每個命令都留痕：QCC 實測失敗時（連不上、命令不認識、密碼錯，
                // 畫面上長得一樣）這份日誌是唯一的線索。命令摘要會遮蔽密碼段。
                _log?.Invoke($"NEMO 連線來自 {remote}");
                var sawData = false;

                try
                {
                    while (true)
                    {
                        // 等待視窗只適用「從未收到任何資料」的連線（QCC 的預覽長連線：
                        // 它在等伺服器先推流）。收到過資料之後必須無限等——QCC 的警報
                        // 心跳 ALARM_HELLO 每 15-20 秒才一發，心跳後 600ms 的靜默會被
                        // 視窗誤判成預覽連線，把警報連線整條交棒掉（運行時實錄）。
                        var count = sawData
                            ? stream.Read(read, 0, read.Length)
                            : NemoSessionLoop.ReadWithPreviewTimeout(stream, read, pending.Length);
                        if (count == 0)
                        {
                            return; // 對方正常關閉
                        }

                        if (count < 0)
                        {
                            // 黃金抓包 §5c（真機行為）：預覽長連線上 QCC 什麼都不送——
                            // 它在等伺服器先推流（TCP 握手後真機立即送 FRAM 0000 0000）。
                            // 等待視窗內沒有任何位元組，直接交棒即時影像通道開始推流；
                            // config/auth 的短連線會立刻送命令，不受視窗影響。
                            _log?.Invoke($"NEMO：等待視窗內沒有資料（{remote}），交棒即時影像通道");
                            NemoSessionLoop.RunSync(
                                stream,
                                pending.ToArray(),
                                remote,
                                _context,
                                _log);
                            return;
                        }

                        sawData = true;
                        pending.Append(read.AsSpan(0, count));

                        // HTTP 前景優先（在 NEMO 解碼之前）：QCV8001 的 NEMO 與 HTTP 同埠
                        // 多工，QCC 的裝置探測先送 HTTP（實測：GET /＋Basic admin:admin）。
                        // 回應後關閉連線——真機實測就是這樣（Connection: close），而且真機
                        // 正是這樣被 QCC 成功加入：QCC 收到 close 後自己開新連線講 NEMO。
                        if (QctekDiagnostics.LooksLikeHttp(pending.Span))
                        {
                            var headerEnd = pending.Span.IndexOf("\r\n\r\n"u8);
                            if (headerEnd < 0)
                            {
                                continue; // 請求未完成：繼續讀
                            }

                            var requestText = Encoding.UTF8.GetString(pending.Span[..(headerEnd + 4)]);
                            _log?.Invoke(
                                $"NEMO：HTTP 請求（{remote}）：{QctekDiagnostics.DescribeBytes(pending.Span[..(headerEnd + 4)])}");
                            var httpResponse = QctekHttpResponder.BuildResponse(
                                requestText,
                                new QctekHttpResponder.Context(
                                    RemoteConsolePort: Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    TotalChannels: _context.ChannelCount));
                            stream.Write(
                                httpResponse is null
                                    ? Encoding.ASCII.GetBytes(
                                        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                                    : Encoding.UTF8.GetBytes(httpResponse));
                            _log?.Invoke($"NEMO：HTTP {(httpResponse is null ? "404" : "200")}（{remote}）");

                            // 真機語意：HTTP 回應即結束這條連線（Connection: close）。
                            return;
                        }

                        // 不是 HTTP 就是 NEMO：把已讀到的位元組交給共用的讀寫迴圈，
                        // 由它接手到連線結束（分幀與認證全在 QctekSession；live 交棒也在裡面）。
                        NemoSessionLoop.RunSync(stream, pending.ToArray(), remote, _context, _log);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // 單一連線的例外（含寫入失敗、伺服器停用時的關閉打斷）不影響伺服器本體。
                    _log?.Invoke($"NEMO 連線結束（{remote}）：{ex.Message}");
                }
            }
        }
        finally
        {
            _connections.TryRemove(socket, out _);
        }
    }

    private static string SafeRemoteEndPoint(TcpClient client)
    {
        try
        {
            return client.Client.RemoteEndPoint?.ToString() ?? "?";
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
