namespace HeliVMS.App.Tests;

/// <summary>
/// 螢幕虛擬鍵盤（<c>Osk.cs</c>）的原始碼契約（M292）。
/// </summary>
/// <remarks>
/// <para>
/// 實際事故：使用者回報「鍵盤叫得出來，但按鍵完全沒反應，也不能切換大小寫」。
/// 桌面端沒有 WPF 執行期測試，而 <c>OskPanel</c> 只能在真的開視窗後才知道對錯，
/// 兩個缺陷都不會編譯錯，只會安靜地讓整個鍵盤不能用：
/// </para>
/// <list type="bullet">
/// <item>拖曳守衛寫成 <c>e.OriginalSource is Button</c>。點按鈕時 <c>OriginalSource</c>
/// 是範本內的 <c>Border</c>／<c>AccessText</c>，永遠不是 <c>Button</c>，於是每個按鍵都
/// 誤觸 <c>DragMove()</c> 的模態迴圈並吃掉 <c>Click</c>——鍵盤因此完全打不出字。</item>
/// <item>版面根本沒有大小寫切換鍵，字母只有大寫，使用者無從切換。</item>
/// </list>
/// <para>
/// 已知邊界：契約只證明「正確的守衛與切換接線存在」，不證明實際點擊行為——那需要
/// WPF 執行期環境；純邏輯（字母集合、大小寫）由
/// <c>HeliVMS.Shared.Tests.OnScreenKeyboardLayoutTests</c> 覆蓋。
/// </para>
/// </remarks>
public sealed class OskContractTests
{
    private const string Code = "src/HeliVMS.App/Osk.cs";

    [Fact]
    public void 拖曳不再以OriginalSource是否為Button判斷()
    {
        var code = SourceContract.Read(Code);

        // 這個判斷永遠不成立，正是「按鍵沒反應」的根因，不得再出現。
        Assert.DoesNotContain("OriginalSource is Button", code);

        // 拖曳只認外框本身；點在按鈕上時事件已被按鈕標記為已處理、不會冒泡到外框。
        Assert.Contains("ReferenceEquals(e.OriginalSource, shell)", code);
    }

    [Fact]
    public void 拖曳改用氣泡階段且仍會實際移動視窗()
    {
        var code = SourceContract.Read(Code);

        Assert.Contains("shell.MouseLeftButtonDown += ", code);
        Assert.DoesNotContain("PreviewMouseLeftButtonDown += OnShellDragStart", code);

        var drag = SourceContract.BodyOf(code, "TryDragMove");
        Assert.NotEqual(string.Empty, drag);
        Assert.Contains("DragMove()", drag);
    }

    [Fact]
    public void 版面由共用純函式提供()
    {
        // 字母集合與大小寫邏輯抽到 HeliVMS.Shared，才測得到；此處釘住確實有接上。
        Assert.Contains("OnScreenKeyboardLayout.Rows(_shift)", SourceContract.Read(Code));
    }

    [Fact]
    public void 按鍵會實際寫入目標輸入框()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnKeyClicked");
        Assert.NotEqual(string.Empty, body);

        Assert.Contains("Osk.AppendInput(Target", body);
        Assert.Contains("OnScreenKeyboardLayout.BackspaceKey", body);
        Assert.Contains("OnScreenKeyboardLayout.ClearKey", body);
    }

    [Fact]
    public void 有大小寫切換鍵且會重建版面()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnKeyClicked");
        Assert.NotEqual(string.Empty, body);

        Assert.Contains("OnScreenKeyboardLayout.ShiftKey", body);
        Assert.Contains("_shift = !_shift", body);
        Assert.Contains("Content = BuildContent()", body);

        // Shift 鍵要出現在版面上（由共用版面提供，這裡確認有對應的標籤與提示）。
        Assert.Contains("Osk.Shift", SourceContract.Read(Code));
    }
}
