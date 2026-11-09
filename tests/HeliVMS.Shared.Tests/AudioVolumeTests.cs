using HeliVMS.Shared;
using Xunit;

namespace HeliVMS.Shared.Tests;

/// <summary>
/// 每攝影機收音音量（QC_Live 的 CameraVolume 對齊）的整備。
/// 守門原因：音量值來自 DB 與滑桿兩個方向，夾值寫錯的症狀是「音量拉到 0 之後整台永遠沒聲音」
/// 或「waveOut 收到負數位準丟例外」；位準換算寫錯則是「拉到最大反而比原本小聲」——
/// 現場只說得出「聲音怪怪的」，純函式把這兩個邊界釘死。
/// </summary>
public sealed class AudioVolumeTests
{
    [Fact]
    public void 任意整數夾到0到100()
    {
        Assert.Equal(0, AudioVolume.Clamp(-1));
        Assert.Equal(0, AudioVolume.Clamp(int.MinValue));
        Assert.Equal(100, AudioVolume.Clamp(101));
        Assert.Equal(100, AudioVolume.Clamp(int.MaxValue));
        Assert.Equal(50, AudioVolume.Clamp(50));
    }

    [Fact]
    public void 未設定回100原樣輸出()
    {
        Assert.Equal(100, AudioVolume.Default);
        Assert.Equal(100, AudioVolume.Clamp(AudioVolume.Default));
    }

    [Fact]
    public void 位準線性對映且0與100對到極值()
    {
        Assert.Equal(0, AudioVolume.ToWaveOutLevel(0));
        Assert.Equal(ushort.MaxValue, AudioVolume.ToWaveOutLevel(100));
        // 50% → 32767.5 → Math.Round（銀行家捲入）＝32768；兩側對稱即可，不必強求哪一邊。
        Assert.Equal((ushort)32768, AudioVolume.ToWaveOutLevel(50));
        Assert.Equal((ushort)0xFFFF, AudioVolume.ToWaveOutLevel(AudioVolume.Max));
        Assert.Equal(0, AudioVolume.ToWaveOutLevel(AudioVolume.Min));
    }

    [Fact]
    public void 越界值先夾再換算不會溢位()
    {
        Assert.Equal(0, AudioVolume.ToWaveOutLevel(-100));
        Assert.Equal(ushort.MaxValue, AudioVolume.ToWaveOutLevel(1000));
    }
}
