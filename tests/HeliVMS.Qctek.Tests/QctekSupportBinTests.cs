using System.Buffers.Binary;
using System.Text;
using HeliVMS.Qctek.Nemo;
using Xunit;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// QCC 的裝置模組端點（/QCSystemStatus.bin 等）的線路格式守衛。
/// QCC 端解析器（qc_center 反編譯）對 SystemStatus 驗「NSSB」魔術＋回應長度
/// 恰 0x400；長度或魔術不對 QCC 會以 3 秒一次無限重試（實測）。
/// </summary>
public class QctekSupportBinTests
{
    [Fact]
    public void 系統狀態回應是NSSB魔術加恰一千零二十四位元組()
    {
        var body = QctekSupportBin.SystemStatus();

        Assert.Equal(QctekSupportBin.SystemStatusLength, body.Length);
        Assert.Equal(QctekSupportBin.SystemStatusLength, 0x400);
        Assert.Equal("NSSB", Encoding.ASCII.GetString(body, 0, 4));
        // 版型欄位（+4 u32）為 0：QCC 只驗魔術與長度，未知欄位零值＝未提供。
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
    }

    [Fact]
    public void 相機狀態回應是NCSB魔術且零列()
    {
        var body = QctekSupportBin.CameraStatus();

        Assert.Equal(12, body.Length);
        Assert.Equal("NCSB", Encoding.ASCII.GetString(body, 0, 4));
        // 0 列是合法狀態：QCC 收到「0 台」不會重試；回錯誤的列反而會讓解析器吃進垃圾。
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void 備份回應帶產品名與公開埠()
    {
        var body = Encoding.UTF8.GetString(QctekSupportBin.BackupSystem("80", 7));

        Assert.Contains("Product=QCV8001", body);
        Assert.Contains("RemoteConsolePort=80", body);
        Assert.Contains("TotalChannel=7", body);
    }

    [Theory]
    [InlineData("/QCSystemStatus.bin", true)]
    [InlineData("/qccamerastatus.bin", true)]   // 路徑比對不分大小寫（QCV8001 用 _stricmp）
    [InlineData("/QCBackupSystem.bin", true)]
    [InlineData("/api/channels", false)]
    [InlineData("/", false)]
    [InlineData("/QCUnknown.bin", false)]
    public void 支援端點路徑的判準與大小寫無關(string path, bool expected)
    {
        Assert.Equal(expected, QctekSupportBin.IsSupportBinPath(path));
    }

    [Fact]
    public void 二進位路由對三端點各回正確內容長度()
    {
        var context = new QctekHttpResponder.Context(RemoteConsolePort: "80", TotalChannels: 3);

        var status = QctekHttpResponder.BuildBinaryBody("/QCSystemStatus.bin", context);
        var camera = QctekHttpResponder.BuildBinaryBody("/QCCameraStatus.bin", context);
        var backup = QctekHttpResponder.BuildBinaryBody("/QCBackupSystem.bin", context);
        var unknown = QctekHttpResponder.BuildBinaryBody("/QCUnknown.bin", context);

        Assert.Equal(0x400, status!.Value.Body.Length);
        Assert.Equal("application/octet-stream", status.Value.ContentType);
        Assert.Equal(12, camera!.Value.Body.Length);
        Assert.NotEmpty(backup!.Value.Body);
        Assert.Null(unknown);
    }
}
