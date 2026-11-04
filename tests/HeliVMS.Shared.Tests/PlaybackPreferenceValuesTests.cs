using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 回放偏好的還原。存的是字串，還原時要能容忍壞值——索引超出目前選單長度時若原樣塞回
/// ComboBox，SelectedIndex 會是 -1（沒有選取），畫面看起來像壞掉。
/// </summary>
public class PlaybackPreferenceValuesTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    public void 布林偏好只認一與零其餘用預設(string? stored, bool expected)
    {
        Assert.Equal(expected, PlaybackPreferenceValues.Bool(stored, fallback: true));
    }

    [Fact]
    public void 布林偏好的預設值可以是關()
    {
        Assert.False(PlaybackPreferenceValues.Bool(null, fallback: false));
        Assert.False(PlaybackPreferenceValues.Bool("garbage", fallback: false));
    }

    [Fact]
    public void 索引偏好落在範圍內時原樣還原()
    {
        Assert.Equal(3, PlaybackPreferenceValues.Index("3", count: 6, fallback: 1));
        Assert.Equal(0, PlaybackPreferenceValues.Index("0", count: 6, fallback: 1));
    }

    [Theory]
    [InlineData("6")]      // 等於選單長度＝越界
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void 索引偏好越界或壞值退回預設(string? stored)
    {
        // 選單改版（例如移除 6 分割）之後，舊偏好值必須退回預設而不是讓 ComboBox 沒有選取。
        Assert.Equal(1, PlaybackPreferenceValues.Index(stored, count: 6, fallback: 1));
    }

    [Fact]
    public void 空選單一律退回預設()
    {
        Assert.Equal(2, PlaybackPreferenceValues.Index("0", count: 0, fallback: 2));
    }
}
