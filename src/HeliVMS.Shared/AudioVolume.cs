namespace HeliVMS.Shared;

/// <summary>
/// 現場收音音量的整備（QC_Live 的 <c>CameraVolume</c> 對齊）。
/// 純函式，行為測試在 <c>AudioVolumeTests</c>。
/// </summary>
/// <remarks>
/// 音量以 0–100 的百分比儲存（<c>channels.listen_volume</c>），套用到 winmm 的
/// <c>waveOutSetVolume</c> 時轉成 0–0xFFFF 的每聲道位準。winmm 的單一 DWORD
/// 同時描述左右聲道（低位＝左、高位＝右），我們永遠兩聲道同值——立體聲平衡
/// 不是這個功能要管的。
/// </remarks>
public static class AudioVolume
{
    /// <summary>音量下限（0＝這台靜音，但不等於關閉收音開關）。</summary>
    public const int Min = 0;

    /// <summary>音量上限（100＝原樣輸出）。</summary>
    public const int Max = 100;

    /// <summary>沒有設定時的音量。</summary>
    public const int Default = 100;

    /// <summary>把任意整數夾到 0–100。</summary>
    public static int Clamp(int percent) => Math.Clamp(percent, Min, Max);

    /// <summary>
    /// 把百分比轉成 <c>waveOutSetVolume</c> 的單聲道位準（0–0xFFFF）。
    /// 線性對映：QC_Live 的音量滑桿就是線性的，聽感一致性以同樣規則為準。
    /// </summary>
    public static ushort ToWaveOutLevel(int percent)
        => (ushort)Math.Round(Clamp(percent) / (double)Max * ushort.MaxValue);
}
