using HeliVMS.Shared.Models;

namespace HeliVMS.Shared;

/// <summary>
/// 監看牆格子右上角「錄影方式指示燈」的色票（純資料；WPF 由桌面端轉成 Brush）。
/// </summary>
/// <remarks>
/// <para>
/// 圖例是操作員要背下來的約定，改色等於改語意：
/// <b>紅＝全時錄影、藍＝排程錄影、綠＝事件（位移）錄影、灰＝手動錄影</b>。
/// 燈全亮＝現在真的有錄影工作階段；壓暗＝這個模式設定如此、但當下不錄
/// （排程時段外、事件尚未觸發、手動待按）。「該錄而沒錄」與「本來就不錄」
/// 在畫面上必須分得出來，這正是操作員回報過的問題。
/// </para>
/// <para>
/// 顏色只是加速辨識，不是唯一管道：左下角徽章同時寫出「全時／排程／事件／手動」的文字，
/// 紅綠難辨的色覺者靠文字與燈的明暗讀到同一件事。
/// </para>
/// </remarks>
public static class WallModeDotPalette
{
    /// <summary>全時錄影（紅）。</summary>
    public const string Always = "#E53935";

    /// <summary>排程錄影（藍）。</summary>
    public const string Scheduled = "#1E88E5";

    /// <summary>事件／位移錄影（綠）。</summary>
    public const string Event = "#43A047";

    /// <summary>手動錄影（灰）。</summary>
    public const string Manual = "#90A4AE";

    /// <summary>燈色；未知的錄影方式回 <see langword="null"/>（呼叫端把燈收起來，不要猜一個顏色）。</summary>
    public static string? For(RecordingMode? mode) => mode switch
    {
        RecordingMode.Always => Always,
        RecordingMode.Scheduled => Scheduled,
        RecordingMode.Event => Event,
        RecordingMode.Manual => Manual,
        _ => null,
    };

    /// <summary>錄影中＝全亮；沒在錄＝壓暗（仍看得出燈色，否則「為什麼不亮」也看不出是哪一種模式）。</summary>
    public static double OpacityFor(bool recording) => recording ? 1.0 : 0.35;
}
