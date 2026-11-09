using System.Buffers.Binary;

namespace HeliVMS.Qctek.Live;

/// <summary>
/// FRAM 封包的線路位元組工廠。欄位與順序由真機 QCV8001 的發送函式
/// （<c>QC_Server</c> 的 <c>FUN_18001cb50</c>）與 QCC 的解析函式
/// （<c>dvrProxy_live::readQCV8001frame</c>）雙向核對。
/// </summary>
/// <remarks>
/// <para>
/// 8-byte 標頭 <c>"FRAM"</c>＋u16 channel（0-based）＋u16 status；status ≠ 0 時
/// <b>只有</b>這 8 bytes（狀態幀，之後沒有 media）。資料幀接 0x38-byte media header
/// 與 payload，順序固定 block1（SPS）→ block2（PPS）→ video（Annex-B）：
/// </para>
/// <list type="bullet">
/// <item><c>+0x00</c> u32 type＝1（視訊）、<c>+0x04</c> u32 codec＝1（H.264）——
/// 客戶端 <c>QC_StreamDec::Put</c> 只收 type＝1。</item>
/// <item><c>+0x08</c> sizeVideo、<c>+0x0c</c> sizeBlock1、<c>+0x10</c> sizeBlock2；
/// 幀總長＝8＋0x38＋block1＋block2＋video（<c>readQCV8001frame</c> 的讀取量算式）。</item>
/// <item><c>+0x14</c> u32 微秒、<c>+0x18</c> u64 Unix 秒（<c>qc_timeval</c> 的線上順序；
/// 客戶端 <c>QC_StreamDec::DecProc</c> 以「秒＋微秒」對牆鐘調速，超出容差的非關鍵幀
/// 會被整包丟棄——所以時戳必須在<b>寫出時</b>以牆鐘蓋章）。</item>
/// <item><c>+0x20</c>/<c>+0x28</c>/<c>+0x30</c> 是 video/block1/block2 的指標欄位：
/// 客戶端依三個 size <b>重算</b>指標（0x40＋block1＋block2），線上值不會被讀，
/// 一律填 0。</item>
/// </list>
/// <para>
/// block1/block2 是「4-byte start code＋NAL」（客戶端 <c>SplitIFrame_H264</c> 切出的
/// 形狀；<c>DummySink::GetSpsPps</c> 同樣以 00 00 00 01 開頭讀），video 保持 Annex-B。
/// <b>block1 size > 0 是客戶端「關鍵影格」的唯一判準</b>：SPS 只放關鍵影格的 block1，
/// 非關鍵影格 block1/block2 必須為 0——SPS 跟著非關鍵幀走會讓客戶端把每一幀都當
/// 關鍵影格，時鐘永遠重設、畫面亂跳。
/// </para>
/// </remarks>
public static class QctekLivePackets
{
    /// <summary>FRAM 標頭（狀態幀的全部內容）長度。</summary>
    public const int HeaderLength = 8;

    /// <summary>media header 長度（0x38）。</summary>
    public const int MediaHeaderLength = 0x38;

    /// <summary>來源已開、尚無影格（真機 live 迴圈的狀態碼）。</summary>
    public const ushort StatusNoFrameYet = 0x8001;

    /// <summary>沒有這個頻道的來源（頻道不存在／無法建立來源）。</summary>
    public const ushort StatusNoSource = 0x8002;

    /// <summary>
    /// 頻道的 proxy session 尚未建立（真機語意：<c>*plVar1 == 0</c>——攝影機有效、
    /// 權限足，但串流代理還沒起來；HeliVMS 對映到「來源曾建立但已結束、重試中」）。
    /// </summary>
    public const ushort StatusNoProxy = 0x8004;

    /// <summary>
    /// 組一個資料幀。時戳由呼叫端在<b>寫出前</b>以牆鐘蓋章（Unix 秒＋當秒內微秒）。
    /// </summary>
    public static byte[] Data(
        int channel,
        ReadOnlySpan<byte> block1,
        ReadOnlySpan<byte> block2,
        ReadOnlySpan<byte> video,
        long unixSeconds,
        int microSeconds)
    {
        var buffer = new byte[HeaderLength + MediaHeaderLength + block1.Length + block2.Length + video.Length];
        WriteHeader(buffer, channel, 0);

        var media = buffer.AsSpan(HeaderLength, MediaHeaderLength);
        BinaryPrimitives.WriteInt32LittleEndian(media, 1);             // type ＝ 視訊
        BinaryPrimitives.WriteInt32LittleEndian(media[0x04..], 1);     // codec ＝ H.264
        BinaryPrimitives.WriteInt32LittleEndian(media[0x08..], video.Length);
        BinaryPrimitives.WriteInt32LittleEndian(media[0x0c..], block1.Length);
        BinaryPrimitives.WriteInt32LittleEndian(media[0x10..], block2.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(media[0x14..], (uint)microSeconds);
        BinaryPrimitives.WriteInt64LittleEndian(media[0x18..], unixSeconds);
        // 指標欄位（+0x20/+0x28/+0x30）維持 0：客戶端以 size 重算，線上值不讀。

        var payload = buffer.AsSpan(HeaderLength + MediaHeaderLength);
        block1.CopyTo(payload);
        block2.CopyTo(payload[block1.Length..]);
        video.CopyTo(payload[(block1.Length + block2.Length)..]);
        return buffer;
    }

    /// <summary>組一個狀態幀：只有 8-byte 標頭，status 非 0。</summary>
    public static byte[] Status(int channel, ushort status)
    {
        var buffer = new byte[HeaderLength];
        WriteHeader(buffer, channel, status);
        return buffer;
    }

    private static void WriteHeader(Span<byte> buffer, int channel, ushort status)
    {
        "FRAM"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..], (ushort)channel);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], status);
    }
}
