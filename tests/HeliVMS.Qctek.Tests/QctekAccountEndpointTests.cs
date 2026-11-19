using System.Buffers.Binary;
using System.Text;
using HeliVMS.Qctek.Nemo;
using Xunit;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// QCC 遠端設定頁的三個帳號寫入端點（<c>/QCRemoveAccount.bin</c>、
/// <c>/QCUpdateAccount.bin</c>、<c>/QCCopyAccount.bin</c>）的線路解析守衛。
/// </summary>
/// <remarks>
/// 這一層刻意做成純函式：HTTP 與資料庫接錯了會留下診斷，線路格式解錯卻只會「QCC 那邊
/// 沒反應」——例如把 base64 的 <c>+</c> 當成空白，刪除／改名就永遠是 400，而且沒有任何
/// 一端會說為什麼。
/// </remarks>
public class QctekAccountEndpointTests
{
    [Theory]
    [InlineData("/QCRemoveAccount.bin", QctekAccountAction.Remove)]
    [InlineData("/qcupdateaccount.bin", QctekAccountAction.Update)]
    [InlineData("/QCCopyAccount.BIN", QctekAccountAction.Copy)]
    [InlineData("/QCSystemStatus.bin", QctekAccountAction.None)]
    [InlineData("/QCCamList.bin", QctekAccountAction.None)]
    [InlineData("/api/accounts", QctekAccountAction.None)]
    [InlineData(null, QctekAccountAction.None)]
    public void 只有三條帳號寫入路徑會被認出來(string? path, QctekAccountAction expected)
    {
        Assert.Equal(expected, QctekAccountEndpoint.Classify(path));
        Assert.Equal(expected != QctekAccountAction.None, QctekAccountEndpoint.IsAccountPath(path));
    }

    [Fact]
    public void 查詢字串只取問號之後且保留base64的正號()
    {
        // QCC 把 base64 直接嵌在 query 裡、未經 URL 編碼（FUN_180002cc0），而標準 base64
        // 會出現 '+'。表單解析會把它換成空白——解碼必然失敗且沒有任何錯誤訊息。
        Assert.Equal("ab+cd/ef==", QctekAccountEndpoint.Payload("?ab+cd/ef=="));
        Assert.Equal("ab+cd", QctekAccountEndpoint.Payload("ab+cd"));

        // 百分號編碼仍要還原（有些代理會把 + 轉成 %2B 再送出）。
        Assert.Equal("ab+cd", QctekAccountEndpoint.Payload("?ab%2Bcd"));
        Assert.Equal("a b", QctekAccountEndpoint.Payload("?a%20b"));
    }

    [Fact]
    public void 沒有查詢字串或問號後是空的都算格式錯誤()
    {
        Assert.Null(QctekAccountEndpoint.Payload(null));
        Assert.Null(QctekAccountEndpoint.Payload(""));
        Assert.Null(QctekAccountEndpoint.Payload("?"));
    }

    [Fact]
    public void 刪除端點會依名稱找usr而不是看第一個參數()
    {
        Assert.True(QctekAccountEndpoint.TryReadRemoveSequence("usr=12", out var sequence));
        Assert.Equal(12, sequence);

        // 真機以 '&' 分項、以 '=' 取名稱，順序不影響。
        Assert.True(QctekAccountEndpoint.TryReadRemoveSequence("x=1&usr=7&y=2", out sequence));
        Assert.Equal(7, sequence);
    }

    [Fact]
    public void usr的值走C的atoi所以垃圾值算零而負數照收()
    {
        // 真機對垃圾值回 0（→ 找不到帳號 → 500），不是「格式錯誤 → 400」。
        Assert.True(QctekAccountEndpoint.TryReadRemoveSequence("usr=abc", out var sequence));
        Assert.Equal(0, sequence);

        // 查詢字串先由 Payload 還原百分號編碼，再交給 atoi（atoi 自己也略過前導空白）。
        Assert.True(QctekAccountEndpoint.TryReadRemoveSequence(
            QctekAccountEndpoint.Payload("?usr=%2012x"), out sequence));
        Assert.Equal(12, sequence);

        Assert.True(QctekAccountEndpoint.TryReadRemoveSequence("usr=-5", out sequence));
        Assert.Equal(-5, sequence);
    }

    [Fact]
    public void usr沒有等號或整個查詢沒有usr時回格式錯誤()
    {
        // 真機在「找到 usr 但沒有 '='」時 break，呼叫端因此回 400。
        Assert.False(QctekAccountEndpoint.TryReadRemoveSequence("usr", out _));
        Assert.False(QctekAccountEndpoint.TryReadRemoveSequence("user=1", out _));
        Assert.False(QctekAccountEndpoint.TryReadRemoveSequence("", out _));
        Assert.False(QctekAccountEndpoint.TryReadRemoveSequence(null, out _));
    }

    [Fact]
    public void base64少了結尾填充時會補上但長度不符仍然拒絕()
    {
        var row = BuildRow(0x08, 5, "viewer");
        var encoded = Convert.ToBase64String(row);

        Assert.NotNull(QctekAccountEndpoint.DecodePayload(encoded, QctekAccountEndpoint.UpdatePayloadLength));

        // QCC 兩種都送過：少一個 '=' 也要解得出來。
        var trimmed = encoded.TrimEnd('=');
        var decoded = QctekAccountEndpoint.DecodePayload(trimmed, QctekAccountEndpoint.UpdatePayloadLength);
        Assert.NotNull(decoded);
        Assert.Equal(row, decoded);

        // 長度不符是真機 qc_base64_decode 的第二個拒絕條件（→ 400）。
        Assert.Null(QctekAccountEndpoint.DecodePayload(encoded, QctekAccountEndpoint.UpdatePayloadLength - 1));
        Assert.Null(QctekAccountEndpoint.DecodePayload("not base64!!", QctekAccountEndpoint.UpdatePayloadLength));
        Assert.Null(QctekAccountEndpoint.DecodePayload(null, QctekAccountEndpoint.UpdatePayloadLength));
    }

    [Fact]
    public void 使用者列讀出版的權限序號與ANSI名稱()
    {
        var row = BuildRow(0x1c, 42, "operator");
        var user = QctekAccountEndpoint.ReadUserRow(row);

        Assert.Equal(0x1c, user.Permission);
        Assert.Equal(42, user.Sequence);
        Assert.Equal("operator", user.Username);
    }

    [Fact]
    public void 使用者名稱遇到NUL就結束而不是把整欄都讀進來()
    {
        var row = new byte[QctekRemoteList.UserRowLength];
        var name = Encoding.Latin1.GetBytes("ab\0cd");
        name.CopyTo(row.AsSpan(0x28, 32));

        Assert.Equal("ab", QctekAccountEndpoint.ReadUserRow(row).Username);
    }

    [Fact]
    public void 複製端點的位圖以LSB優先展開一千零二十四個勾選位()
    {
        var payload = new byte[QctekAccountEndpoint.CopyPayloadLength];
        var bitmap = payload.AsSpan(QctekAccountEndpoint.UpdatePayloadLength);

        bitmap[0] = 0b0000_1001;  // 索引 0 與 3
        bitmap[1] = 0b1000_0000;  // 索引 15
        bitmap[127] = 0b0000_0001; // 索引 1016

        var selection = QctekAccountEndpoint.ReadSelection(payload);

        Assert.Equal(1024, selection.Length);
        Assert.Equal([0, 3, 15, 1016], Enumerable.Range(0, 1024).Where(i => selection[i]));
    }

    [Fact]
    public void 複製權限只換bit0其餘位元不動()
    {
        // 真機：((dest ^ src) & 1) ^ dest——把目標的 bit0 換成來源的 bit0。
        Assert.Equal(0x09, QctekAccountEndpoint.CopyPermission(0x09, 0x08));
        Assert.Equal(0x08, QctekAccountEndpoint.CopyPermission(0x08, 0x08));
        Assert.Equal(0x1d, QctekAccountEndpoint.CopyPermission(0x1d, 0x1c));

        // HeliVMS 的三種權限 bit0 都是 0，所以對我們是 no-op——刻意的，
        // 把 bit0 誤讀成「啟用」會一次停掉所有被勾選的帳號。
        Assert.Equal(0x1c, QctekAccountEndpoint.CopyPermission(0x1c, 0x1c));
        Assert.Equal(0x08, QctekAccountEndpoint.CopyPermission(0x08, 0x08));
    }

    [Theory]
    [InlineData(0x1c, true, true, false)]
    [InlineData(0x08, true, false, true)]
    [InlineData(0x00, false, false, false)]
    [InlineData(0x20, false, false, false)]
    [InlineData(0x3c, true, true, false)]
    public void 權限判定與真機的口徑一致(int permission, bool enabled, bool admin, bool viewer)
    {
        Assert.Equal(enabled, QctekAccountEndpoint.IsEnabled(permission));
        Assert.Equal(admin, QctekAccountEndpoint.IsAdministrator(permission));
        Assert.Equal(viewer, QctekAccountEndpoint.IsViewerClass(permission));
    }

    [Fact]
    public void 查詢裡的hBase64是帳密的base64而不是一般參數()
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:p@ss"));

        Assert.True(QctekAccountEndpoint.TryReadQueryCredentials(
            $"x=1&hBase64={credentials}", out var username, out var password));
        Assert.Equal("admin", username);
        Assert.Equal("p@ss", password);

        // 沒有冒號的 base64 不是帳密（真機以第一個 ':' 切兩段）。
        var noColon = Convert.ToBase64String(Encoding.UTF8.GetBytes("adminonly"));
        Assert.False(QctekAccountEndpoint.TryReadQueryCredentials(
            $"hBase64={noColon}", out _, out _));

        Assert.False(QctekAccountEndpoint.TryReadQueryCredentials("x=1", out _, out _));
    }

    private static byte[] BuildRow(int permission, int sequence, string name)
    {
        var row = new byte[QctekRemoteList.UserRowLength];
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(0x08), permission);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(0x0c), sequence);
        Encoding.Latin1.GetBytes(name).CopyTo(row.AsSpan(0x28));
        return row;
    }
}
