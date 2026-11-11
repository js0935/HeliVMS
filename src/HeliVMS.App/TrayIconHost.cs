namespace HeliVMS.App;

/// <summary>
/// 系統匣常駐（M26）：包裝 <see cref="System.Windows.Forms.NotifyIcon"/>。
/// WPF 內建無 NotifyIcon，此處以 WinForms 組件提供（App.csproj 已啟用 UseWindowsForms）。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly Action _showMain;
    private volatile bool _disposed;

    /// <summary>建立並顯示系統匣圖示；圖示來自目前執行檔（app.ico）。</summary>
    public TrayIconHost(Action showMain)
    {
        _showMain = showMain;
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = ExtractAppIcon(),
            Text = "HeliVMS",
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => _showMain();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var showItem = new System.Windows.Forms.ToolStripMenuItem(Localizer.T("Main.TrayShow"));
        showItem.Click += (_, _) => _showMain();
        menu.Items.Add(showItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var exitItem = new System.Windows.Forms.ToolStripMenuItem(Localizer.T("Main.TrayExit"));
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);
        _icon.ContextMenuStrip = menu;
    }

    /// <summary>
    /// 更新系統匣的停留提示（滑鼠停在圖示上時顯示）。Windows 上限 63 字元，超長截斷。
    /// </summary>
    /// <remarks>
    /// 讓「這是多久之前啟動的實例」任何時刻都看得到：喚醒氣泡只在重複啟動那一次出現
    /// （見 <see cref="MainWindow.ShowWakeBalloon"/>），而操作員平時只會滑過系統匣圖示——
    /// 提示寫著「自 10 天前起執行」，舊行程就無所遁形。字串是 i18n 文案（帶啟動時刻），
    /// 由呼叫端組好傳入；失敗靜靜略過，提示是輔助不是功能。
    /// </remarks>
    public void SetTooltip(string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 63 字元是 Windows 對 NotifyIcon.Text 的硬上限；超過的話設定會靜默失敗或丟例外，
        // 截斷比丟例外好——這一行只該是輔助資訊。
        try
        {
            _icon.Text = text.Length <= 63 ? text : text[..63];
        }
        catch (Exception)
        {
        }
    }

    /// <summary>圖示目前是否可見（於 system tray）。</summary>
    public bool IsVisible => !_disposed && _icon.Visible;

    /// <summary>使用者從匣選單要求結束應用程式。</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// 顯示系統匣氣泡通知。重複啟動被喚醒時，操作員唯一看得到的訊息就是這個：
    /// 視窗「正常地」回到前景，沒有任何線索說明剛才啟動的是<b>舊行程</b>
    /// （見 <see cref="MainWindow.BringToFront"/>）。
    /// </summary>
    public void ShowBalloon(string title, string text)
    {
        if (_disposed || !_icon.Visible)
        {
            return;
        }

        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(8000);
        }
        catch (Exception)
        {
            // 氣泡是輔助提示（RDP 工作階段、無通知中心的環境會丟例外）；失敗不該影響喚醒本身。
        }
    }

    private static System.Drawing.Icon? ExtractAppIcon()
    {
        var path = Environment.ProcessPath;
        if (path is null)
        {
            return null;
        }

        try
        {
            return System.Drawing.Icon.ExtractAssociatedIcon(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}