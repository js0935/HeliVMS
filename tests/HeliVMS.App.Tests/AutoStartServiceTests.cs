using HeliVMS.Shared;

namespace HeliVMS.App.Tests;

/// <summary>
/// 登入自啟。
///
/// 測的是「無人值守的錄影在重開機之後還會不會自己回來」。這件事沒有任何
/// 錯誤訊息可看：設定顯示已啟用、重開機、沒有錄影，而使用者完全無從判斷
/// 是設定沒生效、程式沒跑起來、還是排程本來就沒開那台攝影機。
/// </summary>
public sealed class AutoStartServiceTests
{
    private const string Exe = @"C:\Program Files\HeliVMS\HeliVMS.exe";

    /// <summary>
    /// 啟動命令的路徑一定要加引號。
    ///
    /// 沒有引號的話 <c>C:\Program Files\HeliVMS\HeliVMS.exe</c> 會被 Windows
    /// 當成 <c>"C:\Program"</c> 加上 <c>"Files\HeliVMS\HeliVMS.exe"</c>，
    /// 症狀是「勾了自啟，重開機沒反應」而且不會有任何錯誤。
    /// </summary>
    [Fact]
    public void 啟動命令的路徑會加引號()
    {
        var command = AutoStartService.BuildCommand(Exe);

        Assert.StartsWith($"\"{Exe}\"", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// 自啟要縮到系統匣，不能跳監控視窗。
    ///
    /// 自啟存在的意義是無人值守錄影；登入時跳一個滿版攝影機的視窗，
    /// 等於宣告「這台機器剛開機就有人在用」，而且會擋住畫面。
    /// </summary>
    [Fact]
    public void 啟動命令固定帶縮到匣的參數()
    {
        Assert.Contains(
            AutoStartService.TrayArgument,
            AutoStartService.BuildCommand(Exe),
            StringComparison.Ordinal);
    }

    [Fact]
    public void 啟用後讀回來是啟用()
    {
        var store = new FakeStore();
        var service = new AutoStartService(store, Exe);

        Assert.False(service.IsEnabled());
        service.SetEnabled(true);
        Assert.True(service.IsEnabled());
        Assert.Equal(AutoStartService.BuildCommand(Exe), store.Command);
    }

    /// <summary>關閉要真的把登錄值刪掉，否則使用者取消勾選後重開機還是會自己啟動。</summary>
    [Fact]
    public void 停用後讀回來是停用()
    {
        var store = new FakeStore();
        var service = new AutoStartService(store, Exe);
        service.SetEnabled(true);

        service.SetEnabled(false);

        Assert.False(service.IsEnabled());
        Assert.Null(store.Command);
    }

    /// <summary>重複啟用不該讓狀態壞掉（設定頁每次勾選都會呼叫）。</summary>
    [Fact]
    public void 重複啟用仍然維持啟用()
    {
        var store = new FakeStore();
        var service = new AutoStartService(store, Exe);

        service.SetEnabled(true);
        service.SetEnabled(true);

        Assert.True(service.IsEnabled());
    }

    /// <summary>有設定但內容是空白時視為未啟用，否則畫面會顯示已啟用但其實沒東西可跑。</summary>
    [Fact]
    public void 空白登錄值視為未啟用()
    {
        var service = new AutoStartService(new FakeStore { Command = "   " }, Exe);

        Assert.False(service.IsEnabled());
    }

    private sealed class FakeStore : IAutoStartStore
    {
        public string? Command { get; set; }

        public string? Read() => Command;

        public void Write(string command) => Command = command;

        public void Delete() => Command = null;
    }
}