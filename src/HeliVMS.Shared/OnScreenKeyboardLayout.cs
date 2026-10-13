namespace HeliVMS.Shared;

/// <summary>
/// 螢幕虛擬鍵盤（OSK）的版面與大小寫邏輯（M292）。
/// </summary>
/// <remarks>
/// 這層刻意做成純函式、不碰 WPF：OSK 是 <c>OskPanel</c> 這種只能實際開視窗才知道對錯的
/// 元件，而測試專案（HeliVMS.App.Tests）依既有慣例不參考 WPF 應用程式。把「預設小寫、
/// Shift 切大寫」與「五個特殊鍵齊備」搬到這裡，才能用單元測試釘住；否則大小寫切換鍵
/// 漏掉或字母集合不完整都不會有任何測試轉紅。
/// </remarks>
public static class OnScreenKeyboardLayout
{
    /// <summary>切換大小寫的黏著鍵（按一下切換，非按住）。</summary>
    public const string ShiftKey = "shift";

    /// <summary>空白鍵。</summary>
    public const string SpaceKey = "space";

    /// <summary>清空目前輸入。</summary>
    public const string ClearKey = "clear";

    /// <summary>退格。</summary>
    public const string BackspaceKey = "backspace";

    /// <summary>收起鍵盤。</summary>
    public const string CollapseKey = "collapse";

    private const string Qwerty = "QWERTYUIOP";
    private const string Home = "ASDFGHJKL";
    private const string Bottom = "ZXCVBNM";

    /// <summary>
    /// 依大小寫狀態回傳每一列按鍵；字母為單一字元、特殊鍵為上面的常數。
    /// <paramref name="shift"/> 為 false 時輸出小寫（帳密輸入較常見），true 時輸出大寫。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Rows(bool shift)
    {
        return
        [
            ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
            Letters(Qwerty, shift),
            [.. Letters(Home, shift), BackspaceKey],
            [ShiftKey, .. Letters(Bottom, shift), ".", "@", "_"],
            [SpaceKey, ClearKey, CollapseKey],
        ];
    }

    /// <summary>字母鍵的輸出：預設小寫，Shift 時大寫。</summary>
    public static string[] Letters(string upper, bool shift)
    {
        var text = shift ? upper : upper.ToLowerInvariant();
        return text.Select(c => c.ToString()).ToArray();
    }
}
