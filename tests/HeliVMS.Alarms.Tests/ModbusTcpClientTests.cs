using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HeliVMS.Alarms.Tests;

public class ModbusTcpClientTests
{
    [Fact]
    public void BuildReadRequest_EncodesMbapAndFc02()
    {
        var frame = ModbusTcpClient.BuildReadRequest(tid: 0x1234, unit: 0x01, fc: 0x02, start: 0, count: 8);

        Assert.Equal(new byte[] { 0x12, 0x34, 0x00, 0x00, 0x00, 0x06, 0x01, 0x02, 0x00, 0x00, 0x00, 0x08 },
            frame);
    }

    [Fact]
    public void BuildWriteCoilRequest_EncodesFc05OnOff()
    {
        var on = ModbusTcpClient.BuildWriteCoilRequest(tid: 0x0001, unit: 0x10, address: 4, on: true);
        var off = ModbusTcpClient.BuildWriteCoilRequest(tid: 0x0001, unit: 0x10, address: 4, on: false);

        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x10, 0x05, 0x00, 0x04, 0xFF, 0x00 }, on);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x10, 0x05, 0x00, 0x04, 0x00, 0x00 }, off);
    }

    [Fact]
    public void ParseReadResponse_ExtractsBitsAcrossBytes()
    {
        // bit1 與 bit3 為 1（byte0=0b00001010）；最高位在第 9 bit（byte1=0b00000001）
        var pdu = new byte[] { 0x02, 0x02, 0x0A, 0x01 };

        var bits = ModbusTcpClient.ParseReadResponse(pdu, fc: 0x02, count: 10);

        Assert.Equal([false, true, false, true, false, false, false, false, true, false], bits);
    }

    [Fact]
    public void ParseReadResponse_ExceptionFrame_Throws()
    {
        var pdu = new byte[] { 0x82, 0x01 }; // fc02 | 0x80?�ILLEGAL FUNCTION

        var ex = Assert.Throws<InvalidOperationException>(() => ModbusTcpClient.ParseReadResponse(pdu, fc: 0x02, count: 1));
        Assert.Contains("code=", ex.Message);
    }

    [Fact]
    public void ParseReadResponse_ShortByteCount_Throws()
    {
        var pdu = new byte[] { 0x02, 0x01, 0x00 };

        Assert.Throws<InvalidOperationException>(() => ModbusTcpClient.ParseReadResponse(pdu, fc: 0x02, count: 14));
    }

    [Fact]
    public void ParseWriteCoilResponse_MatchesEcho()
    {
        var echo = new byte[] { 0x05, 0x00, 0x04, 0xFF, 0x00 };

        Assert.True(ModbusTcpClient.ParseWriteCoilResponse(echo, address: 4, on: true));
        Assert.False(ModbusTcpClient.ParseWriteCoilResponse(echo, address: 5, on: true));
        Assert.False(ModbusTcpClient.ParseWriteCoilResponse(echo, address: 4, on: false));
    }

    [Fact]
    public async Task Loopback_ReadDiscreteInputs_EndToEnd()
    {
        await using var server = await StartFakeAsync(frame =>
        {
            var rsp = new byte[frame.Length];
            Array.Copy(frame, rsp, 7);
            rsp[5] = (byte)(3 + 2);      // len = unit(1)+pdu(3): fc+byteCount+2bytes
            rsp[6] = frame[6];           // unit
            rsp[7] = 0x02;               // fc echo
            rsp[8] = 0x02;               // byteCount
            rsp[9] = 0x0A;
            rsp[10] = 0x01;
            return rsp;
        });

        var client = new ModbusTcpClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(2));
        var bits = await client.ReadDiscreteInputsAsync(unitId: 1, start: 0, count: 10);

        Assert.Equal([false, true, false, true, false, false, false, false, true, false], bits);
    }

    [Fact]
    public async Task Loopback_WriteSingleCoil_GetsEchoTrue()
    {
        await using var server = await StartFakeAsync(frame => frame);   // ?�接 echo

        var client = new ModbusTcpClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(2));
        Assert.True(await client.WriteSingleCoilAsync(unitId: 1, address: 4, on: true));
    }

    [Fact]
    public async Task Loopback_ExceptionRespond_Throws()
    {
        await using var server = await StartFakeAsync(frame =>
        {
            var rsp = new byte[10];
            Array.Copy(frame, rsp, 7);
            rsp[5] = 3;                  // len = unit(1)+pdu(2)
            rsp[6] = frame[6];
            rsp[7] = (byte)(frame[7] | 0x80);
            rsp[8] = 0x02;
            return rsp;
        });

        var client = new ModbusTcpClient("127.0.0.1", server.Port, TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.WriteSingleCoilAsync(unitId: 1, address: 4, on: true));
    }

    private static async Task<FakeModbusServer> StartFakeAsync(Func<byte[], byte[]> respond)
    {
        var server = new FakeModbusServer(respond);
        await server.StartAsync();
        return server;
    }

    private sealed class FakeModbusServer : IAsyncDisposable
    {
        private readonly Func<byte[], byte[]> _respond;
        private TcpListener? _listener;

        public FakeModbusServer(Func<byte[], byte[]> respond)
        {
            _respond = respond;
        }

        public int Port { get; private set; }

        public async Task StartAsync()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient? client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                        _ = HandleAsync(client);
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                }
            });
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var header = new byte[7];
                await stream.ReadExactlyAsync(header);
                var len = (header[4] << 8) | header[5];
                var body = new byte[len - 1];
                await stream.ReadExactlyAsync(body);
                var frame = new byte[7 + body.Length];
                Array.Copy(header, frame, 7);
                body.CopyTo(frame, 7);
                var rsp = _respond(frame);
                await stream.WriteAsync(rsp);
                await stream.FlushAsync();
            }
        }

        public ValueTask DisposeAsync()
        {
            _listener?.Stop();
            return ValueTask.CompletedTask;
        }
    }
}