using System.Buffers.Binary;
using System.Text;
using HeliVMS.Qctek.Nemo;
using Xunit;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// QCC 遠端設定頁的兩份清單（<c>/QCCamList.bin</c> = RMCL、
/// <c>/QCUserList.bin</c> = RMUL）的線路格式守衛。
/// </summary>
/// <remarks>
/// 驗證端是 <c>QC_rsClient::CheckCameraList</c>／<c>CheckUserList</c>：magic、列長
/// （0x11c／0x228）與「總長＝列數×列長＋0x10」三者逐位元組比對，且
/// <c>QueryUserSetting</c> 一次抓兩支端點——任一壞掉就整頁空白（兩邊日誌都乾淨）。
/// 所以這些數字不是「大致對就好」。
/// </remarks>
public class QctekRemoteListTests
{
    [Fact]
    public void 相機清單標頭是RMCL且總長等於列數乘列長加十六()
    {
        var body = QctekRemoteList.CameraList(
        [
            new QctekRemoteCamera("攝影機01"),
            new QctekRemoteCamera("攝影機02"),
        ]);

        Assert.Equal(QctekRemoteList.CameraMagic, ReadU32(body, 0));
        Assert.Equal("RMCL", Encoding.ASCII.GetString(body, 0, 4));
        Assert.Equal((uint)body.Length, ReadU32(body, 4));
        Assert.Equal(0x11c, QctekRemoteList.CameraRowLength);
        Assert.Equal(0x10 + (2 * 0x11c), body.Length);
        Assert.Equal(2u, ReadU32(body, 8));
        Assert.Equal(0x11cu, ReadU32(body, 0x0c));
    }

    [Fact]
    public void 使用者清單標頭是RMUL且列長是五百五十二()
    {
        var body = QctekRemoteList.UserList(
        [
            new QctekRemoteUser("admin", QctekRemoteList.AdminPermission),
        ]);

        Assert.Equal(QctekRemoteList.UserMagic, ReadU32(body, 0));
        Assert.Equal("RMUL", Encoding.ASCII.GetString(body, 0, 4));
        Assert.Equal((uint)body.Length, ReadU32(body, 4));
        Assert.Equal(1u, ReadU32(body, 8));
        Assert.Equal(0x228, QctekRemoteList.UserRowLength);
        Assert.Equal(0x228u, ReadU32(body, 0x0c));
        Assert.Equal(0x10 + 0x228, body.Length);
    }

    [Fact]
    public void 零列清單仍然是一個完整的十六位元組標頭()
    {
        var cameras = QctekRemoteList.CameraList([]);
        var users = QctekRemoteList.UserList([]);

        Assert.Equal(0x10, cameras.Length);
        Assert.Equal(0u, ReadU32(cameras, 8));
        Assert.Equal(0x10u, ReadU32(cameras, 4));

        Assert.Equal(0x10, users.Length);
        Assert.Equal(0u, ReadU32(users, 8));
        Assert.Equal(0x10u, ReadU32(users, 4));
    }

    [Fact]
    public void 相機列的名稱與群組各在反編譯位移且其餘欄位是Excel預設值()
    {
        var body = QctekRemoteList.CameraList([new QctekRemoteCamera("前門")]);
        var row = body.AsSpan(0x10, 0x11c);

        // 名稱 wchar[0x41]（0x82 bytes）、群組緊接在後；反編譯的 +0x82 是 wchar 位移，
        // 換成位元組就是 0x104。
        Assert.Equal("前門", Encoding.Unicode.GetString(row[..0x82]).TrimEnd('\0'));
        Assert.Equal(string.Empty, Encoding.Unicode.GetString(row.Slice(0x82, 0x82)).TrimEnd('\0'));

        // Excel 欄 2/3/4/5 在列不存在時寫 -1，欄 7（顏色）是真機預設色。
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(row[0x104..]));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(row[0x108..]));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(row[0x110..]));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(row[0x114..]));
        Assert.Equal(QctekRemoteList.DefaultCameraColor, BinaryPrimitives.ReadInt32LittleEndian(row[0x10c..]));
        Assert.Equal(QctekRemoteList.DefaultCameraColor, 0x00ff0100);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(row[0x118..]));
    }

    [Fact]
    public void 超長的名稱會截斷但仍以NUL結尾而不是切出半個字()
    {
        var name = new string('A', 200) + "尾";
        var body = QctekRemoteList.CameraList([new QctekRemoteCamera(name)]);
        var field = body.AsSpan(0x10, 0x82);

        var decoded = Encoding.Unicode.GetString(field);
        Assert.Equal(0x82 / 2, decoded.Length);
        Assert.Equal(new string('A', 64), decoded[..64]);
        Assert.Equal('\0', decoded[64]);
        Assert.EndsWith("\0", decoded);
    }

    [Fact]
    public void 使用者列的名稱寫在偏移四十且完全不帶密碼雜湊()
    {
        var body = QctekRemoteList.UserList(
        [
            new QctekRemoteUser("operator", QctekRemoteList.DefaultPermission),
        ]);
        var row = body.AsSpan(0x10, 0x228);

        Assert.Equal(QctekRemoteList.UserRowMagic, BinaryPrimitives.ReadUInt32LittleEndian(row));
        Assert.Equal(0xede9u, QctekRemoteList.UserRowMagic);
        // 真機 user.bin 的 +0x04 是一個 u32＝0x001E0003：低 16 位是版本 3、高 16 位是
        // 旗標 0x1E。兩個欄位共用同一個 u32，所以這裡讀整個字組，不只看低半部。
        Assert.Equal(0x001e0003u, BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
        Assert.Equal(QctekRemoteList.UserRowVersion, BinaryPrimitives.ReadUInt16LittleEndian(row[4..]));
        Assert.Equal(QctekRemoteList.UserRowFlags, BinaryPrimitives.ReadUInt16LittleEndian(row[6..]));

        // 名稱是 char[32]（FindAccountIndexByName 逐 byte 比對），不是寬字元。
        Assert.Equal("operator", Encoding.ASCII.GetString(row.Slice(0x28, 32)).TrimEnd('\0'));

        // +0x48 起是真機的 32-byte 密碼雜湊。HeliVMS 不外流雜湊，一律 0。
        Assert.True(row.Slice(0x48, 32).IndexOfAnyExcept((byte)0) < 0);

        // +0x10／+0x18／+0x20 是建立／到期／修改時間：不知道就留 0，不編一個假到期日。
        Assert.True(row.Slice(0x10, 0x18).IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void 目前登入者會加上權限位元的二十()
    {
        var body = QctekRemoteList.UserList(
        [
            new QctekRemoteUser("admin", QctekRemoteList.AdminPermission, IsCurrent: true),
            new QctekRemoteUser("viewer", QctekRemoteList.DefaultPermission),
        ]);

        var first = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0x10 + 0x08));
        var second = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0x10 + 0x228 + 0x08));

        Assert.Equal(0x1c | 0x20, first);
        Assert.Equal(0x20, QctekRemoteList.CurrentUserBit);
        Assert.Equal(0x08, second);
    }

    [Theory]
    [InlineData(true, true, 0x1c)]
    [InlineData(true, false, 0x08)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 0)]
    public void 權限換算停用沒有權限且admin對齊真機(bool enabled, bool admin, int expected)
    {
        Assert.Equal(expected, QctekRemoteList.PermissionFor(enabled, admin));
    }

    [Fact]
    public void 非ASCII的帳號名稱寫成問號而不是讓裝置端讀到亂碼()
    {
        var body = QctekRemoteList.UserList(
        [
            new QctekRemoteUser("管理員", QctekRemoteList.AdminPermission),
        ]);

        var field = body.AsSpan(0x10 + 0x28, 32);
        Assert.Equal("???", Encoding.ASCII.GetString(field[..3]));
        Assert.True(field[3..].IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void 列數超過驗證端上限時會截到一千零二十三列()
    {
        var users = Enumerable.Range(0, QctekRemoteList.MaxRowCount + 5)
            .Select(i => new QctekRemoteUser($"u{i}", QctekRemoteList.DefaultPermission))
            .ToList();
        var body = QctekRemoteList.UserList(users);

        Assert.Equal(1023, QctekRemoteList.MaxRowCount);
        Assert.Equal(0x3ffu, ReadU32(body, 8));
        Assert.Equal((uint)body.Length, ReadU32(body, 4));
        Assert.Equal(0x10 + (0x3ff * 0x228), body.Length);
    }

    private static uint ReadU32(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
}
