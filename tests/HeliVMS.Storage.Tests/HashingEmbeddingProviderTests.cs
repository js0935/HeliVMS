using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>
/// 決定性雜湊嵌入器（§14.1 #8 後備實作）：無模型、可離線，且必須跨行程穩定，
/// 否則重啟後以不同雜湊檢索已索引向量會全部落空。
/// </summary>
public sealed class HashingEmbeddingProviderTests
{
    [Fact]
    public void 維度與模型識別固定()
    {
        var provider = new HashingEmbeddingProvider(64);

        Assert.Equal(64, provider.Dimension);
        Assert.Equal(64, provider.Embed("前門").Count);
        Assert.Contains("64", provider.ModelId);
    }

    [Fact]
    public void 相同文字跨實例產生相同向量()
    {
        // 兩個獨立實例代表兩次行程：若改用 string.GetHashCode()，此處會因雜湊加鹽而不等。
        var a = new HashingEmbeddingProvider(32);
        var b = new HashingEmbeddingProvider(32);

        Assert.Equal(a.Embed("入侵偵測 alarm").ToArray(), b.Embed("入侵偵測 alarm").ToArray());
    }

    [Fact]
    public void 非空文字輸出為單位向量()
    {
        var vector = new HashingEmbeddingProvider(48).Embed("front door motion");
        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));

        Assert.Equal(1.0, norm, 6);
    }

    [Fact]
    public void 空字串回全零向量且長度正確()
    {
        var vector = new HashingEmbeddingProvider(16).Embed(string.Empty);

        Assert.Equal(16, vector.Count);
        Assert.All(vector, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void 共用字元較多的文字餘弦相似度較高()
    {
        var provider = new HashingEmbeddingProvider();

        var query = provider.Embed("前門卡片進出");
        var near = provider.Embed("前門卡片進入");
        var far = provider.Embed("後門強行開啟");

        Assert.True(Cosine(query, near) > Cosine(query, far));
    }

    [Fact]
    public void 空白與大小寫不影響向量()
    {
        var provider = new HashingEmbeddingProvider(24);

        Assert.Equal(provider.Embed("Front Door").ToArray(), provider.Embed(" front   door ").ToArray());
    }

    [Fact]
    public void 向量值固定可跨行程重現()
    {
        // Golden：把演算法輸出的精確值釘住。若改用 string.GetHashCode()（每個行程隨機加鹽）
        // 或更動權重／維度語意，這裡會立刻失敗——這才是「跨行程、跨版本穩定」真正的守門，
        // 單靠同行程建立兩個實例比對擋不住加鹽。
        var vector = new HashingEmbeddingProvider(4).Embed("ab");

        Assert.Equal(new[] { -0.4082483f, -0.4082483f, 0.8164966f, 0f }, vector.ToArray());
    }

    [Fact]
    public void 維度必須為正()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashingEmbeddingProvider(0));
    }

    private static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
