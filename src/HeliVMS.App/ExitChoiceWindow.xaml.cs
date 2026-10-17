using System.Windows;

namespace HeliVMS.App;

/// <summary>關閉主視窗時使用者選擇的動作。</summary>
public enum ExitChoice
{
    /// <summary>收進系統匣，背景繼續錄影。</summary>
    Minimize,

    /// <summary>結束 HeliVMS。</summary>
    Quit,

    /// <summary>離開全螢幕</summary>
    ExitFullScreen,

    /// <summary>不關閉（取消或直接關掉本對話框）。</summary>
    Cancel,
}

/// <summary>
/// 關閉主視窗時的「最小化／離開」三選一對話框。
/// </summary>
/// <remarks>
/// 主視窗關閉鍵原本靜默收進系統匣，操作員按了「X」會以為程式已經結束，實際上還在背景錄影；
/// 反之，想真正結束時又只有系統匣選單一條路。這裡把兩種意圖明講出來，讓使用者當下決定。
/// 系統匣選單的「結束 HeliVMS」維持直接結束（那本身已是明示），不經此對話框。
/// </remarks>
public partial class ExitChoiceWindow : Window
{
    /// <summary>使用者的選擇；對話框被關掉（X／Esc）時為 <see cref="ExitChoice.Cancel"/>。</summary>
    public ExitChoice Choice { get; private set; } = ExitChoice.Cancel;

    public ExitChoiceWindow()
    {
        InitializeComponent();
        ApplyI18n();
    }

    private void ApplyI18n()
    {
        Title = Localizer.T("Exit.Title");
        MessageText.Text = Localizer.T("Exit.Message");
        ExitFullScreenButton.Content = Localizer.T("Main.ExitFullScreen");
        MinimizeButton.Content = Localizer.T("Exit.Minimize");
        QuitButton.Content = Localizer.T("Exit.Quit");
        CancelButton.Content = Localizer.T("Exit.Cancel");
    }

    /// <summary>以 <paramref name="owner"/> 為 owner 顯示模態對話框並回傳選擇。</summary>
    public static ExitChoice Ask(Window owner)
    {
        var dialog = new ExitChoiceWindow();
        if (owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return dialog.Choice;
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.Minimize;
        DialogResult = true;
    }

    private void OnQuitClicked(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.Quit;
        DialogResult = true;
    }

    private void OnExitFullScreenClicked(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.ExitFullScreen;
        DialogResult = true;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        Choice = ExitChoice.Cancel;
        DialogResult = false;
    }
}
