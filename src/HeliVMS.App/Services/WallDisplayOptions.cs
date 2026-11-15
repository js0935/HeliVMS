namespace HeliVMS.App;

/// <summary>監看牆上可由操作員開關的顯示項目。</summary>
/// <remarks>
/// 這些開關原本是頂欄的五顆 <c>ToggleButton</c>／<c>CheckBox</c>，與「回放／事件／地圖」這種
/// <b>入口</b>混在一起：頂欄同時塞了「去哪」與「畫面上要不要出現某個元件」兩種語意，
/// 一排按鈕十幾顆，操作員找不到回放。入口留在頂欄，顯示偏好移到設定中心（功能頁）。
/// </remarks>
public enum WallDisplayOption
{
    /// <summary>頻道快速列（牆的右緣）。</summary>
    QuickBar,

    /// <summary>牆上事件面板（牆的左緣）。</summary>
    EventPanel,

    /// <summary>浮動警報窗。</summary>
    AlarmPopup,

    /// <summary>第二螢幕鏡像牆。</summary>
    SecondScreen,

    /// <summary>AI 偵測框疊加（需 <c>ai</c> 授權）。</summary>
    AiOverlay,
}

/// <summary>
/// 設定中心改動監看牆顯示項目時走的中介。
/// </summary>
/// <remarks>
/// 狀態的唯一持有者是 <c>MainWindow</c>（它有 <c>_quickBarEnabled</c>／<c>_aiVisible</c> 等欄位，
/// 以及解碼執行緒與鏡像窗）。設定視窗若直接持有 <c>MainWindow</c>，就成了第二個寫入點，
/// 而且兩個視窗互相引用會讓生命週期糾纏（設定窗關閉時不該動到牆）。
///
/// <para>
/// <see cref="SetWallDisplayOption"/> 回傳的是<b>實際生效</b>值，不是要求值：AI 疊加沒有
/// <c>ai</c> 授權、第二螢幕打不開（只有一顆螢幕）時勾選要退回，否則設定頁會說謊，
/// 操作員勾了卻什麼都沒發生。設定頁在收到回傳值後自行修正勾選狀態；
/// 由牆面自己改變的（鏡像窗被 ✕ 關掉）則透過 <see cref="WallDisplayOptionChanged"/> 回拋。
/// </para>
/// </remarks>
public interface IWallDisplayOptions
{
    /// <summary>目前是否啟用（回傳牆面真正的狀態，不是設定值的回音）。</summary>
    bool GetWallDisplayOption(WallDisplayOption option);

    /// <summary>設定並回傳<b>實際生效</b>的結果。</summary>
    bool SetWallDisplayOption(WallDisplayOption option, bool enabled);

    /// <summary>牆面自己改動了某個顯示項目（例如鏡像窗被關閉），設定頁據此同步勾選。</summary>
    event EventHandler<WallDisplayOption>? WallDisplayOptionChanged;
}
