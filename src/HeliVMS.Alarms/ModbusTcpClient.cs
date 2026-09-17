using System.Net.Sockets;

namespace HeliVMS.Alarms;

/// <summary>
/// Modbus/TCP 傳輸面（§16.2 網路 IO 模組）。手編 MBAP＋PDU，無第三方依賴：
/// FC02 讀離散輸入（DI）、FC01 讀線圈（DO 狀態）、FC05 寫單一線圈（DO）。
/// 每次作業開臨時 TCP 連線（同一模組小量 IO，簡單可靠）。
/// </summary>
public sealed class ModbusTcpClient : IModbusTcpClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;

    public ModbusTcpClient(string host, int port, TimeSpan? timeout = null)
    {
        _host = host;
        _port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>FC02 讀離散輸入，回傳自 start 起共 count 個位元。</summary>
    public async Task<IReadOnlyList<bool>> ReadDiscreteInputsAsync(
        byte unitId, ushort start, ushort count, CancellationToken ct = default)
    {
        using var s = await OpenAsync(ct).ConfigureAwait(false);
        var tid = (ushort)Random.Shared.Next(0, 0x10000);
        await s.Stream.WriteAsync(BuildReadRequest(tid, unitId, 0x02, start, count), ct).ConfigureAwait(false);
        await s.Stream.FlushAsync(ct).ConfigureAwait(false);
        var pdu = await ReadPduAsync(s.Stream, tid, ct).ConfigureAwait(false);
        return ParseReadResponse(pdu, fc: 0x02, count);
    }

    /// <summary>FC01 讀線圈（DO 狀態），回傳自 start 起共 count 個位元。</summary>
    public async Task<IReadOnlyList<bool>> ReadCoilsAsync(
        byte unitId, ushort start, ushort count, CancellationToken ct = default)
    {
        using var s = await OpenAsync(ct).ConfigureAwait(false);
        var tid = (ushort)Random.Shared.Next(0, 0x10000);
        await s.Stream.WriteAsync(BuildReadRequest(tid, unitId, 0x01, start, count), ct).ConfigureAwait(false);
        await s.Stream.FlushAsync(ct).ConfigureAwait(false);
        var pdu = await ReadPduAsync(s.Stream, tid, ct).ConfigureAwait(false);
        return ParseReadResponse(pdu, fc: 0x01, count);
    }

    /// <summary>FC05 寫單一線圈（DO）。回應須 echo 請求才算成功。</summary>
    public async Task<bool> WriteSingleCoilAsync(
        byte unitId, ushort address, bool on, CancellationToken ct = default)
    {
        using var s = await OpenAsync(ct).ConfigureAwait(false);
        var tid = (ushort)Random.Shared.Next(0, 0x10000);
        await s.Stream.WriteAsync(BuildWriteCoilRequest(tid, unitId, address, on), ct).ConfigureAwait(false);
        await s.Stream.FlushAsync(ct).ConfigureAwait(false);
        var pdu = await ReadPduAsync(s.Stream, tid, ct).ConfigureAwait(false);
        return ParseWriteCoilResponse(pdu, address, on);
    }

    /// <summary>組 FC01/FC02 讀請求（MBAP＋PDU）。</summary>
    public static byte[] BuildReadRequest(ushort tid, byte unit, byte fc, ushort start, ushort count)
    {
        var pdu = new byte[] { fc, (byte)(start >> 8), (byte)start, (byte)(count >> 8), (byte)count };
        return BuildFrame(tid, unit, pdu);
    }

    /// <summary>組 FC05 寫線圈請求（位元組序 FFFF=on／0000=off）。</summary>
    public static byte[] BuildWriteCoilRequest(ushort tid, byte unit, ushort address, bool on)
    {
        var pdu = new byte[] { 0x05, (byte)(address >> 8), (byte)address, on ? (byte)0xFF : (byte)0x00, 0x00 };
        return BuildFrame(tid, unit, pdu);
    }

    /// <summary>解讀回覆 PDU 為位元序列（依 byteCount）。</summary>
    public static IReadOnlyList<bool> ParseReadResponse(byte[] pdu, byte fc, int count)
    {
        if (pdu.Length < 2 || pdu[0] != fc)
        {
            ThrowModbus(pdu);
        }

        var byteCount = pdu[1];
        if (byteCount < (count + 7) / 8)
        {
            throw new InvalidOperationException($"Modbus 回覆位元組數不足：{byteCount} < {(count + 7) / 8}");
        }

        var bits = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var b = pdu[2 + (i / 8)];
            bits[i] = (b & (1 << (i % 8))) != 0;
        }

        return bits;
    }

    /// <summary>比對 FC05 回應是否 echo 請求（TID 已由 frame 層確認）。</summary>
    public static bool ParseWriteCoilResponse(byte[] pdu, ushort address, bool on)
    {
        if (pdu.Length != 5 || pdu[0] != 0x05)
        {
            ThrowModbus(pdu);
        }

        var addr = (ushort)((pdu[1] << 8) | pdu[2]);
        return addr == address
            && pdu[3] == (on ? (byte)0xFF : (byte)0x00)
            && pdu[4] == 0x00;
    }

    private async Task<StreamHolder> OpenAsync(CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            await client.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
            client.NoDelay = true;
            return new StreamHolder(client, client.GetStream());
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed record StreamHolder(TcpClient Tcp, NetworkStream Stream) : IDisposable
    {
        public void Dispose()
        {
            Stream.Dispose();
            Tcp.Dispose();
        }
    }

    private static byte[] BuildFrame(ushort tid, byte unit, byte[] pdu)
    {
        var len = checked((ushort)(pdu.Length + 1));
        var frame = new byte[7 + pdu.Length];
        frame[0] = (byte)(tid >> 8);
        frame[1] = (byte)tid;
        frame[2] = 0x00;      // protocol id hi
        frame[3] = 0x00;      // protocol id lo
        frame[4] = (byte)(len >> 8);
        frame[5] = (byte)len;
        frame[6] = unit;
        pdu.CopyTo(frame, 7);
        return frame;
    }

    private async Task<byte[]> ReadPduAsync(NetworkStream stream, ushort tid, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, cts.Token).ConfigureAwait(false);
        var rspTid = (ushort)((header[0] << 8) | header[1]);
        if (rspTid != tid)
        {
            throw new InvalidOperationException($"Modbus 回應 TID 不符：{rspTid} != {tid}");
        }

        var len = (header[4] << 8) | header[5];
        if (len < 1 || len > 255)
        {
            throw new InvalidOperationException($"Modbus 回應長度異常：{len}");
        }

        var body = new byte[len - 1]; // 扣除 UnitId
        await stream.ReadExactlyAsync(body, cts.Token).ConfigureAwait(false);
        return body;
    }

    private static void ThrowModbus(byte[] pdu)
    {
        if (pdu.Length >= 2 && (pdu[0] & 0x80) != 0)
        {
            throw new InvalidOperationException($"Modbus 例外回應：function={(pdu[0] & 0x7F):X2} code={pdu[1]}");
        }

        throw new InvalidOperationException($"Modbus 回應格式不符（len={pdu.Length}, fc={pdu[0]:X2}）");
    }
}

/// <summary>IO 模組傳輸面（供 IoDeviceMonitor 注入；測試可替換）。</summary>
public interface IModbusTcpClient
{
    /// <summary>FC02 讀離散輸入（DI 狀態）。</summary>
    Task<IReadOnlyList<bool>> ReadDiscreteInputsAsync(byte unitId, ushort start, ushort count, CancellationToken ct = default);

    /// <summary>FC01 讀線圈（DO 狀態）。</summary>
    Task<IReadOnlyList<bool>> ReadCoilsAsync(byte unitId, ushort start, ushort count, CancellationToken ct = default);

    /// <summary>FC05 寫單一線圈（DO 控制）。</summary>
    Task<bool> WriteSingleCoilAsync(byte unitId, ushort address, bool on, CancellationToken ct = default);
}