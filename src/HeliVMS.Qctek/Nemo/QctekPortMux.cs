using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HeliVMS.Qctek.Nemo;

/// <summary>前置多工器在一條連線上認出的協定。</summary>
public enum QctekMuxProtocol
{
    /// <summary>位元組還不足以判斷（命令或請求行尚未收完）。</summary>
    NeedMore,

    /// <summary>QCTek NEMO 控制協定（由本行程的 <see cref="QctekSession"/> 應答）。</summary>
    Nemo,

    /// <summary>HTTP（轉發給內部的 Kestrel）。</summary>
    Http,
}

/// <summary>
/// Web 埠的前置多工器：讓 NEMO 與 HTTP <b>共用同一個埠</b>，與真機 QCV8001 一致。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要它：QCC 連的是「裝置的 Web 埠」（QCV8001 預設 80），而真機在 80 上以
/// <c>HttpServer::PreParseRequest</c> 分辨 NEMO／HTTP。HeliVMS 的 80 是 Kestrel，
/// 它只會回 400 並關線——QCC 端看到的就是「連線失敗」。
/// <c>getparam.cgi</c> 的 <c>RemoteConsolePort</c> 導向已證實不可行（真機也不靠它）。
/// </para>
/// <para>
/// 做法：本機迴圈的 <see cref="TcpListener"/> 綁在公開埠上，先讀幾個位元組分類：
/// NEMO 交給 <see cref="NemoSessionLoop"/>（與 5170 的 <see cref="NemoServer"/> 同一套
/// 狀態機），HTTP 原封不動轉發到內部的 Kestrel 臨時埠。為了保留
/// <c>RemoteConsolePort</c> 的對外語意，HTTP 轉發時由桌面端的 Web 主機以
/// 「公開埠」設定告知 Kestrel（見 <see cref="QctekHttpResponder.PublicPortConfigKey"/>），
/// 而不是事後改寫請求。
/// </para>
/// <para>
/// 仍然只綁 loopback：對外的管理介面不該因為多工而變成公開服務；遠端 CMS 請走
/// 獨立埠的 <see cref="NemoServer"/>。
/// </para>
/// </remarks>
public sealed class QctekPortMux : IAsyncDisposable
{
    /// <summary>命令動詞的長度上限（最長的是 <c>QCV8001_AUTH_SENDCODE</c>／<c>CHECK_SERVER_VERSION</c>）。</summary>
    public const int MaxVerbLength = 32;

    /// <summary>HTTP 方法：命令後面一定接空白，NEMO 的命令則帶底線或整串大寫。</summary>
    private static readonly string[] HttpMethods =
        ["GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "PATCH", "TRACE", "PRI"];

    private readonly IQctekDeviceContext _context;
    private readonly IPAddress _address;
    private readonly int _requestedPort;
    private readonly int _httpPort;
    private readonly Action<string>? _log;
    private readonly TimeSpan _classificationIdleTimeout;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Thread? _acceptThread;
    private readonly ConcurrentDictionary<Socket, byte> _connections = new();

    /// <param name="context">裝置資訊（版本、頻道數…），供 <see cref="QctekSession"/> 使用。</param>
    /// <param name="httpPort">內部 Kestrel 的實際埠（HTTP 轉發目標）。</param>
    /// <param name="address">綁定位址；預設 loopback。</param>
    /// <param name="port">公開埠；<c>0</c>＝由系統指派（測試用）。</param>
    /// <param name="log">可選的訊息記錄。</param>
    /// <param name="classificationIdleTimeout">
    /// 分類前等第一包的上限；<c>null</c>＝<see cref="InitialReadGate.DefaultIdleTimeout"/>。
    /// </param>
    public QctekPortMux(
        IQctekDeviceContext context,
        int httpPort,
        IPAddress? address = null,
        int port = 0,
        Action<string>? log = null,
        TimeSpan? classificationIdleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (httpPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(httpPort), "內部 HTTP 埠必須是 1–65535。");
        }

        _context = context;
        _httpPort = httpPort;
        _address = address ?? IPAddress.Loopback;
        _requestedPort = port;
        _log = log;
        _classificationIdleTimeout = classificationIdleTimeout ?? InitialReadGate.DefaultIdleTimeout;
    }

    /// <summary>實際綁定的公開埠（<c>port: 0</c> 時由系統指派）。</summary>
    public int Port { get; private set; }

    /// <summary>HTTP 轉發目標（內部 Kestrel 的埠）。</summary>
    public int InternalHttpPort => _httpPort;

    /// <summary>開始接受連線（非阻塞）。</summary>
    /// <exception cref="InvalidOperationException">已啟動。</exception>
    public void Start()
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("QCTek Web 埠多工器已啟動。");
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
            Name = "qctek-mux-accept",
        };
        _acceptThread.Start();
        _log?.Invoke($"MUX listening on {_address}:{Port}（HTTP → 127.0.0.1:{_httpPort}）");
    }

    /// <summary>停止接受連線並等待接受迴圈結束。</summary>
    public async Task StopAsync()
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        _cts?.Cancel();
        listener.Stop();
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

    /// <summary>
    /// 依目前已讀到的位元組判斷這是 NEMO 還是 HTTP。
    /// <para>
    /// 判斷必須是<b>純函式</b>且容忍「只收到一半」：TCP 不保證一次讀到完整的命令或
    /// 請求行，太早猜錯的代價是連線被送到錯的處理器（NEMO 收到 HTTP 會直接關線，
    /// 而 QCC 只會說「連線失敗」）。
    /// </para>
    /// </summary>
    public static QctekMuxProtocol Classify(ReadOnlySpan<byte> data)
    {
        var spaceAt = -1;
        for (var i = 0; i < data.Length && i < MaxVerbLength; i++)
        {
            var b = data[i];
            if (b == (byte)' ')
            {
                spaceAt = i;
                break;
            }

            // 命令與 HTTP 方法都是可列印 ASCII；出現控制字元／非 ASCII 代表這不是我們
            // 認得的協定，直接交給 HTTP 端（Kestrel 會回 400 並關線，至少不留半開連線）。
            if (b is < 0x21 or > 0x7E)
            {
                return QctekMuxProtocol.Http;
            }
        }

        if (spaceAt < 0)
        {
            // 動詞還沒收完：除非已經長到不可能是任何命令，否則再等。
            return data.Length >= MaxVerbLength ? QctekMuxProtocol.Http : QctekMuxProtocol.NeedMore;
        }

        var verb = data[..spaceAt];

        foreach (var method in HttpMethods)
        {
            if (verb.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(method)))
            {
                return QctekMuxProtocol.Http;
            }
        }

        // CONNECT 同時是 HTTP 方法與 NEMO 命令（CONNECT <user> <base64 pass> ）。
        // HTTP 的請求行是 "CONNECT host:port HTTP/1.x"；NEMO 的 raw 命令以 NUL 結尾、
        // 不含 CRLF——兩個特徵都足以分辨，且不必等第二個空白（NEMO 的密碼可能很短）。
        if (verb.SequenceEqual("CONNECT"u8))
        {
            if (data.IndexOf(" HTTP/"u8) >= 0)
            {
                return QctekMuxProtocol.Http;
            }

            return data.IndexOf((byte)0) >= 0 || data.IndexOf("\r\n"u8) >= 0
                ? QctekMuxProtocol.Nemo
                : data.Length >= MaxVerbLength * 4
                    ? QctekMuxProtocol.Nemo
                    : QctekMuxProtocol.NeedMore;
        }

        // 其餘大寫動詞一律視為 NEMO（CHECK_SERVER_VERSION、GET_CONFIG_QCTEK、NEMO…）。
        return QctekMuxProtocol.Nemo;
    }

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
                _log?.Invoke($"MUX accept 失敗：{ex.SocketErrorCode}");
                break;
            }

            // 專用執行緒（非執行緒池）＋同步 I/O：回應時效是協定行為的一部分，見 DedicatedThread。
            DedicatedThread.Run(() => HandleConnection(client), "qctek-mux-conn");
        }
    }

    private void HandleConnection(TcpClient client)
    {
        var socket = client.Client;
        _connections[socket] = 0;
        var remote = SafeRemoteEndPoint(client);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            using (client)
            {
                client.NoDelay = true;
                // 每一條被接受的連線都留痕。「QCC 根本沒連進來」與「連進來但一個位元組
                // 都沒送」在日誌上曾經長成同一件事（什麼都沒有），而 2026-11-14 的
                // cfg=0 即時影像嘗試正好落在這兩者之間、無從分辨。
                _log?.Invoke($"MUX：接受連線（{remote}）");
                var stream = client.GetStream();
                var pending = new NemoSessionLoop.FrameBuffer();
                var read = new byte[8192];
                var received = 0;

                try
                {
                    var protocol = QctekMuxProtocol.NeedMore;
                    while (protocol == QctekMuxProtocol.NeedMore)
                    {
                        // 分類前先沉默：HTTP 請求與 NEMO 命令都會先送位元組，靜默不是
                        // 任何一種協定的開場。舊版把「600ms 視窗內分類不出協定」當成
                        // 「等伺服器先推流的預覽線」交棒給 live 通道——那條路把自家
                        // 警報連線灌滿影片（2026-11-14 逐包實錄）。
                        //
                        // 但「沉默」也不能無限等：Read 是同步阻塞，每條連線佔一條專用
                        // 執行緒，QCC 斷線後留下的半開連線會把它永遠佔住。
                        var status = InitialReadGate.Read(
                            client,
                            stream,
                            read,
                            _classificationIdleTimeout,
                            out var count);

                        if (status == InitialReadStatus.TimedOut)
                        {
                            _log?.Invoke(
                                $"MUX：連線在分類前閒置逾時（{remote}）：{Elapsed(startedAt)}ms、" +
                                $"已收 {received} bytes，關閉");
                            return;
                        }

                        if (status == InitialReadStatus.Closed)
                        {
                            // 對端連上就關線、一個位元組都沒送：這正是「QCC 有沒有把
                            // live 連線真的送到這裡」的唯一證據，不能靜默結束。
                            _log?.Invoke(
                                $"MUX：連線未送出位元組就結束（{remote}）：{Elapsed(startedAt)}ms、" +
                                $"已收 {received} bytes");
                            return;
                        }

                        received += count;
                        pending.Append(read.AsSpan(0, count));
                        protocol = Classify(pending.Span);
                    }

                    if (protocol == QctekMuxProtocol.Http)
                    {
                        _log?.Invoke($"MUX：HTTP（{remote}）→ 127.0.0.1:{_httpPort}");
                        RelayHttp(client, pending.ToArray());
                        return;
                    }

                    _log?.Invoke(
                        $"MUX：NEMO 連線來自 {remote}（分類前已收 {received} bytes、" +
                        $"{Elapsed(startedAt)}ms）");
                    NemoSessionLoop.RunSync(stream, pending.ToArray(), remote, _context, _log);
                }
                catch (Exception ex)
                {
                    // 單一連線的例外不影響多工器本體（含伺服器停用時的關閉打斷）。
                    _log?.Invoke($"MUX 連線結束（{remote}）：{ex.Message}");
                }
            }
        }
        finally
        {
            _connections.TryRemove(socket, out _);
        }
    }

    /// <summary>從接受到現在的毫秒數（只給診斷行用）。</summary>
    private static string Elapsed(long startedAt)
        => Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds.ToString("F0");

    /// <summary>
    /// 把連線原封不動轉給內部 Kestrel（同步版）。兩個方向各自半關閉（<c>Shutdown(Send)</c>）：
    /// 單向 <c>Close</c> 會把另一個方向還在傳的回應截斷。
    /// </summary>
    private void RelayHttp(TcpClient client, byte[] initial)
    {
        using var upstream = new TcpClient();
        upstream.NoDelay = true;
        try
        {
            upstream.Connect(IPAddress.Loopback, _httpPort);
            var downstream = client.GetStream();
            var up = upstream.GetStream();

            // 為了分類而先讀掉的位元組要先補送，否則 Kestrel 收到的是半個請求行。
            if (initial.Length > 0)
            {
                up.Write(initial, 0, initial.Length);
            }

            // 半關閉的是「目標」的 socket：來源讀完＝這個方向沒有更多資料，
            // 要讓目標知道（客戶端才看得到 EOF，而不是等我們關整條連線）。
            // HTTP 轉發是短生命週期的 I/O 對拷，兩個方向各一條專用執行緒——
            // 不排執行緒池佇列（見 DedicatedThread 的說明）。
            var toUpstream = new Thread(
                () => Pump(downstream, up, upstream.Client))
            {
                IsBackground = true,
                Name = "qctek-mux-http-up",
            };
            var toClient = new Thread(
                () => Pump(up, downstream, client.Client))
            {
                IsBackground = true,
                Name = "qctek-mux-http-down",
            };
            toUpstream.Start();
            toClient.Start();
            toUpstream.Join();
            toClient.Join();
        }
        catch (SocketException ex)
        {
            _log?.Invoke($"MUX：HTTP 轉發失敗（{ex.SocketErrorCode}）");
        }
        catch (IOException ex)
        {
            _log?.Invoke($"MUX：HTTP 轉發中斷（{ex.Message}）");
        }
    }

    /// <summary>單向搬運；來源結束即對目標半關閉（讓對方知道沒有更多資料）。</summary>
    private static void Pump(Stream source, Stream target, Socket targetSocket)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var count = source.Read(buffer, 0, buffer.Length);
                if (count == 0)
                {
                    break;
                }

                target.Write(buffer, 0, count);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            targetSocket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            // 對方已關閉：沒有東西可關。
        }
        catch (ObjectDisposedException)
        {
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
