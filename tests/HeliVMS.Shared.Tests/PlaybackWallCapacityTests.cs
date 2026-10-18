using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 回放牆容量的行為契約：能同時播的路數是「要求的分割數」與「實際頻道數」取小。
/// </summary>
/// <remarks>
/// 現場問「最多能同時播幾分割」時，答案不是 UI 的 9／16，而是頻道數——每格一個解碼程序，
/// 第 5 格以後沒有來源。這條測試就是那個答案的可執行版本，避免日後有人把上限直接寫成分割數。
/// </remarks>
public sealed class PlaybackWallCapacityTests
{
    [Fact]
    public void 要求九分割但只有四台時只開四路()
    {
        Assert.Equal(4, PlaybackWallCapacity.Effective(9, 4));
    }

    [Fact]
    public void 頻道足夠時開到要求的分割數()
    {
        Assert.Equal(16, PlaybackWallCapacity.Effective(16, 32));
        Assert.Equal(2, PlaybackWallCapacity.Effective(2, 2));
    }

    [Fact]
    public void 沒有頻道時不開任何格()
    {
        Assert.Equal(0, PlaybackWallCapacity.Effective(4, 0));
        Assert.Equal(0, PlaybackWallCapacity.Effective(0, 4));
    }
}
