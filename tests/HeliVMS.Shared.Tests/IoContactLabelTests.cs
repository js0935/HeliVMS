using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 接點名稱必須看得出「哪一顆模組（或哪一台攝影機）的哪個接點」——這是規則下拉、地圖設定頁
/// 與地圖圖釘共用的唯一一份拼法。
/// </summary>
/// <remarks>
/// 現場需求是「加一顆 ADAM-6250，要在 IO 裡看到 DI/DO、能在警報規則裡用、能在地圖上顯示」。
/// 兩顆模組的通道名都是 <c>DI 1</c>／<c>DI 2</c>，若名稱不帶來源，規則下拉裡就是兩個一模一樣的
/// 選項；選錯一顆等於驅動錯的繼電器，而且事後看規則清單也分不出來。
/// </remarks>
public class IoContactLabelTests
{
    [Fact]
    public void 接點名稱前面帶上來原名稱()
        => Assert.Equal("ADAM-6250 · DI#3 一樓鐵門", IoContactLabel.Port("DI", 3, "一樓鐵門", "ADAM-6250"));

    [Fact]
    public void 攝影機端子會帶上攝影機名稱()
        => Assert.Equal("攝影機01 · DO#2 RelayOutput2", IoContactLabel.Port("DO", 2, "RelayOutput2", "攝影機01"));

    [Fact]
    public void 沒有來源時維持舊格式()
    {
        // 沒綁攝影機的舊接點、或攝影機已被刪除：仍然要顯示得出來，而不是空白或只寫分隔線。
        Assert.Equal("DI#1 di_i1", IoContactLabel.Port("DI", 1, "di_i1", null));
        Assert.Equal("DO#2 RelayOutput2", IoContactLabel.Port("DO", 2, "RelayOutput2", "   "));
    }

    [Fact]
    public void 模組通道帶上模組名稱()
        => Assert.Equal("ADAM-6250 · DI 1", IoContactLabel.ModuleChannel("DI 1", "ADAM-6250", 3));

    [Fact]
    public void 模組名稱空白時退回模組編號()
        => Assert.Equal("#3 · DI 1", IoContactLabel.ModuleChannel("DI 1", null, 3));

    [Fact]
    public void 兩顆模組的同號接點名稱不一樣()
    {
        // 這一條就是「選錯繼電器」的守門測試：名字若只取通道名，兩者會相等。
        var first = IoContactLabel.ModuleChannel("DI 1", "ADAM-6250", 3);
        var second = IoContactLabel.ModuleChannel("DI 1", "ADAM-6250 (192.168.0.61)", 4);

        Assert.NotEqual(first, second);
    }
}
