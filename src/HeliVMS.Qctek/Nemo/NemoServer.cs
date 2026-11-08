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
    private Task? _acceptLoop;

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
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
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

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
            _acceptLoop = null;
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

            // 連線各自獨立；HandleConnectionAsync 內部吞掉所有例外。
            _ = Task.Run(() => HandleConnectionAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var pending = new FrameBuffer();
            var read = new byte[8192];
            var remote = SafeRemoteEndPoint(client);
            var session = new QctekSession(_context);

            // 每個連線與每個命令都留痕：QCC 實測失敗時（連不上、命令不認識、密碼錯，
            // 畫面上長得一樣）這份日誌是唯一的線索。命令摘要會遮蔽密碼段。
            _log?.Invoke($"NEMO 連線來自 {remote}");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int count;
                    try
                    {
                        count = await stream.ReadAsync(read.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (IOException)
                    {
                        return; // 對方斷線（RST 等）
                    }

                    if (count == 0)
                    {
                        return; // 對方正常關閉
                    }

                    pending.Append(read.AsSpan(0, count));

                    while (true)
                    {
                        // HTTP 前景優先（在 NEMO 解碼之前）：QCV8001 的 NEMO 與 HTTP 同埠
                        // 多工，QCC 的裝置探測先送 HTTP（實測：GET /＋Basic admin:admin）。
                        // 回應後關閉連線——真機實測就是這樣（Connection: close），而且真機
                        // 正是這樣被 QCC 成功加入：QCC 收到 close 後自己開新連線講 NEMO。
                        // （先前的「keep-alive 留在連線上續等」是對照真機前的推測。）
                        if (QctekDiagnostics.LooksLikeHttp(pending.Span))
                        {
                            var headerEnd = pending.Span.IndexOf("\r\n\r\n"u8);
                            if (headerEnd < 0)
                            {
                                break; // 請求未完成：繼續讀
                            }

                            var requestText = Encoding.UTF8.GetString(pending.Span[..(headerEnd + 4)]);
                            _log?.Invoke(
                                $"NEMO：HTTP 請求（{remote}）：{QctekDiagnostics.DescribeBytes(pending.Span[..(headerEnd + 4)])}");
                            var httpResponse = QctekHttpResponder.BuildResponse(
                                requestText,
                                new QctekHttpResponder.Context(
                                    RemoteConsolePort: Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    TotalChannels: _context.ChannelCount));
                            await stream.WriteAsync(
                                httpResponse is null
                                    ? Encoding.ASCII.GetBytes(
                                        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                                    : Encoding.UTF8.GetBytes(httpResponse),
                                cancellationToken).ConfigureAwait(false);
                            _log?.Invoke($"NEMO：HTTP {(httpResponse is null ? "404" : "200")}（{remote}）");
                            pending.Consume(headerEnd + 4);

                            // 真機語意：HTTP 回應即結束這條連線。實測 QCV8001 對 GET /
                            // 回 Connection: close 並關閉，而真機正是這樣被 QCC 成功加入。
                            // 標頭說 close 卻不關，會讓對方等我們關（或自己超時）。
                            return;
                        }

                        var result = session.Advance(pending.Span);
                        if (result.Close)
                        {
                            // 串流不同步：直接關閉，不猜測重新同步點。傾印原始位元組——
                            // 實測（QCC 加裝置）時，唯一能回答「對方送了什麼」的就是這一行
                            // （HTTP 探測、別的協定、或損壞的串流一眼可辨）。
                            _log?.Invoke(
                                $"NEMO：非 NEMO 資料（{remote}），關閉連線。前 96 bytes：" +
                                QctekDiagnostics.DescribeBytes(pending.Span));
                            return;
                        }

                        if (result.Consumed == 0)
                        {
                            break; // 半個訊框：繼續讀
                        }

                        if (result.Reply is null)
                        {
                            if (result.Command is not null)
                            {
                                // 未知命令不回覆（維持 null 語意）——但一定要留痕：
                                // 這是「CMS 發了我們不認識的命令」唯一的證據。
                                _log?.Invoke(
                                    $"NEMO ← {QctekDiagnostics.Summarize(result.Command)}（未知命令，不回應）");
                            }
                        }
                        else
                        {
                            _log?.Invoke($"NEMO ← {QctekDiagnostics.Summarize(result.Command)}");
                            await stream.WriteAsync(result.Reply.AsMemory(), cancellationToken).ConfigureAwait(false);
                            _log?.Invoke($"NEMO → {QctekDiagnostics.DescribeReply(result.Reply)}");
                        }

                        pending.Consume(result.Consumed);
                    }
                }
            }
            catch (Exception ex)
            {
                // 單一連線的例外（含寫入失敗）不影響伺服器本體。
                _log?.Invoke($"NEMO 連線結束（{remote}）：{ex.Message}");
            }
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

    /// <summary>連線的累積緩衝：附加、取用、消耗前綴（量小，位移即可）。</summary>
    private sealed class FrameBuffer
    {
        private byte[] _buffer = new byte[8192];
        private int _length;

        public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

        public void Append(ReadOnlySpan<byte> data)
        {
            if (_length + data.Length > _buffer.Length)
            {
                var grown = new byte[Math.Max(_buffer.Length * 2, _length + data.Length)];
                _buffer.AsSpan(0, _length).CopyTo(grown);
                _buffer = grown;
            }

            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }

        public void Consume(int count)
        {
            if (count >= _length)
            {
                _length = 0;
                return;
            }

            _buffer.AsSpan(count, _length - count).CopyTo(_buffer);
            _length -= count;
        }
    }
}
