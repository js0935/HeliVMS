using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// NEMO 回應的「線路位元組」工廠：把每種回應型式的標頭與結尾 NUL 一次講清楚。
/// </summary>
/// <remarks>
/// <para>
/// 真機（QCV8001）對不同命令使用<b>不同的框法</b>，不是一套通用規則；這些型式由
/// <c>QC_Server</c> 的回應函式（<c>FUN_180008ce0</c>、<c>FUN_1800062f0</c>、
/// <c>FUN_180006550</c> 等）逐位元組還原，並以 QCC ↔ 真機的黃金抓包（<c>golden.pcapng</c>）
/// 對照過：
/// </para>
/// <list type="bullet">
/// <item><b>raw 文字</b>：命令本身不帶 NEMO 標頭，回應是文字＋結尾 NUL
/// （<c>CHECK_SERVER_VERSION</c>、<c>ALARM_REQUEST</c>…）。</item>
/// <item><b>NEMO 文字＋NUL</b>：標頭只宣告文字長度，NUL 寫在宣告長度之外
/// （<c>ALARM_ISALIVE</c>、<c>ALARM_CANCEL</c>）——標頭<b>少宣告 1</b>。</item>
/// <item><b>NEMO 文字、無 NUL</b>：<c>CONNECT</c>、<c>GET_MAX_STREAM_NUM</c>；
/// 標頭長度就是實際寫出的位元組數。</item>
/// <item><b>NEMO 二進位</b>：<c>NEMO &lt;payloadLen&gt;</c>＋剛好 payloadLen bytes，無 NUL
/// （<c>GET_CONFIG_QCTEK</c>、<c>GET_AUTH_USER</c>、<c>GET_IOCONFIG</c>）。</item>
/// </list>
/// <para>
/// 為什麼要獨立成一個型別：長度只要差 1（多寫或漏寫 NUL），對端的讀取器就會把下一個回應的
/// 開頭吞掉或卡住等資料，而畫面只會顯示「連線失敗」——把「哪一種框法」變成具名方法，
/// 才不會在命令處理器裡用字串相加各自猜。
/// </para>
/// </remarks>
public static class QctekReply
{
    /// <summary>raw 文字回應：文字＋結尾 NUL（QCC 的 raw 命令用）。</summary>
    public static byte[] Raw(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.ASCII.GetBytes(text);
        var buffer = new byte[bytes.Length + 1];
        bytes.CopyTo(buffer, 0);
        return buffer;
    }

    /// <summary>raw 位元組回應：原樣輸出，不補 NUL（認證挑戰字串用）。</summary>
    public static byte[] RawExact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>NEMO 文字回應、無結尾 NUL（標頭長度＝文字長度）。</summary>
    public static byte[] Framed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return NemoMessage.Encode(Encoding.ASCII.GetBytes(text));
    }

    /// <summary>
    /// NEMO 文字回應＋結尾 NUL：標頭宣告文字長度，NUL 是第 <c>len+1</c> 個位元組
    /// （真機標頭少宣告 1；照抄，不要「修正」）。
    /// </summary>
    public static byte[] FramedWithNul(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.ASCII.GetBytes(text);
        var buffer = new byte[NemoMessage.HeaderLength + bytes.Length + 1];
        NemoMessage.Encode(bytes).CopyTo(buffer, 0);
        return buffer;
    }

    /// <summary>NEMO 二進位回應：標頭長度＝payload 長度，無 NUL。</summary>
    public static byte[] FramedBinary(ReadOnlySpan<byte> payload) => NemoMessage.Encode(payload);
}
