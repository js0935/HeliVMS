using System.Net;
using System.Net.Sockets;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 音訊接收器（<see cref="AudioStreamReceiver"/>）的行為契約。
/// </summary>
/// <remarks>
/// <para>
/// 這一層接的是「解碼 ffmpeg 多吐一份 PCM 到本機 UDP」。三個保證各自對應一種現場症狀：
/// 埠必須在 ffmpeg 啟動前就綁好（否則聲音整條不見）、跨封包要能拼回 100 ms（否則播放
/// 會一段一段爆音）、開始前的舊資料要丟掉（否則按下喇叭先播一兩秒前的聲音，聽起來像
/// 聲音慢了半拍，而畫面完全正常）。
/// </para>
/// <para>
/// 綁 socket 的測試一律進 <see cref="MediaIoCollection"/>：xunit 會平行跑測試類別，
/// 兩個類別各自挑埠時會互相搶（AGENTS 的踩雷記錄）。
/// </para>
/// </remarks>
[Collection(MediaIoCollection.Name)]
public sealed class AudioStreamReceiverTests
{
    [Fact]
    public void 埠在開始接收前就已綁定()
    {
        using var receiver = new AudioStreamReceiver();

        Assert.True(receiver.Port is > 0 and <= 65535);
        Assert.Equal(3200, receiver.ChunkBytes);
        Assert.False(receiver.IsRunning);

        // 沒有在讀也要能收（ffmpeg 從第一秒就在送，操作員可能一分鐘後才按喇叭）。
        Assert.True(receiver.IsRunning == false);
    }

    [Fact]
    public async Task 位元組流會跨封包拼成完整片段()
    {
        using var receiver = new AudioStreamReceiver();
        var chunks = new List<byte[]>();
        receiver.ChunkReady += (_, chunk) =>
        {
            lock (chunks)
            {
                chunks.Add(chunk);
            }
        };

        receiver.Start();
        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, receiver.Port);

        Send(sender, target, Pattern(1000, 1));   // 1000 + 2200 = 一個完整片段
        Send(sender, target, Pattern(2200, 2));
        Send(sender, target, Pattern(5000, 3));   // 再一個片段，餘 1800

        await WaitForAsync(() => Count(chunks) >= 2);

        lock (chunks)
        {
            Assert.Equal(2, chunks.Count);
            Assert.Equal(3200, chunks[0].Length);
            Assert.Equal(3200, chunks[1].Length);
        }

        // 停止時剩下的 1800 bytes 仍要送出來（少播最後 60 ms，比卡著好）。
        receiver.Stop();
        await WaitForAsync(() => Count(chunks) >= 3);

        lock (chunks)
        {
            Assert.Equal(1800, chunks[2].Length);
        }
    }

    [Fact]
    public async Task 開始前的舊資料會被丟掉()
    {
        using var receiver = new AudioStreamReceiver();
        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, receiver.Port);

        // 模擬「聲音通道還沒打開，ffmpeg 已經在送」的那一段。
        Send(sender, target, Pattern(3200, 7));
        await Task.Delay(50);

        var chunks = new List<byte[]>();
        receiver.ChunkReady += (_, chunk) =>
        {
            lock (chunks)
            {
                chunks.Add(chunk);
            }
        };
        receiver.Start();

        Send(sender, target, Pattern(3200, 9));
        await WaitForAsync(() => Count(chunks) >= 1);
        await Task.Delay(100);

        lock (chunks)
        {
            Assert.Single(chunks);
            Assert.Equal(9, chunks[0][0]);   // 舊資料（值 7）不能出現
        }
    }

    [Fact]
    public async Task 沒有資料時不會回報已收到()
    {
        using var receiver = new AudioStreamReceiver();
        receiver.Start();
        await Task.Delay(100);

        Assert.Equal(0, receiver.BytesReceived);
        Assert.Equal(0, receiver.ChunksReceived);
        Assert.Equal(DateTime.MinValue, receiver.LastDataUtc);

        receiver.Stop();
        Assert.False(receiver.IsRunning);
    }

    [Fact]
    public void 埠範圍外的取樣率會被擋下()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioStreamReceiver(sampleRate: 4000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioStreamReceiver(chunkMilliseconds: 1));
    }

    private static byte[] Pattern(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    private static void Send(UdpClient sender, IPEndPoint target, byte[] data) =>
        sender.Send(data, data.Length, target);

    private static int Count(List<byte[]> chunks)
    {
        lock (chunks)
        {
            return chunks.Count;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }
    }
}
