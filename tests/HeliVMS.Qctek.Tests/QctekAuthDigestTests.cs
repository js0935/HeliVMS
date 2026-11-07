using HeliVMS.Qctek.Nemo;

namespace HeliVMS.Qctek.Tests;

/// <summary>NEMO CLIENT_AUTH 摘要：MD5 ＋字組位元組反轉＋自訂字母表。</summary>
public class QctekAuthDigestTests
{
    [Fact]
    public void 已知向量符合反編譯還原的演算法()
    {
        // 手算樣本（Python 逐步重現同一演算法）：
        // md5("admin:1234:admin") = df0cac582746cb56fe0e0a2d49da95f2
        // → 字組位元組反轉＋abcdef0123456789 字母表 → 下值。
        // 這條測試釘住的是「演算法」不是實作：改動任何一步（雜湊、反轉、字母表）都會紅。
        var digest = QctekAuthDigest.Compute("admin", "1234", "admin");
        Assert.Equal("f246a679f065e0c1c7a4a8989c3f74e3", digest);
    }

    [Fact]
    public void 摘要固定32字元且只用自訂字母表()
    {
        var digest = QctekAuthDigest.Compute("root", "challenge", "p@ssw0rd");
        Assert.Equal(32, digest.Length);
        Assert.All(digest, ch => Assert.Contains(ch, QctekAuthDigest.Alphabet));
    }

    [Fact]
    public void 挑戰值不同摘要就不同()
    {
        // middle（挑戰）參與雜湊：同一組帳密在不同挑戰下必須得到不同回覆，
        // 否則重放攻擊只要記下 32 字元就能登入。
        var a = QctekAuthDigest.Compute("admin", "1234", "admin");
        var b = QctekAuthDigest.Compute("admin", "5678", "admin");
        Assert.NotEqual(a, b);
    }
}
