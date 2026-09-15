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
        var showItem = new System.Windows.Forms.ToolStripMenuItem("顯示主視窗");
        showItem.Click += (_, _) => _showMain();
        menu.Items.Add(showItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("結束 HeliVMS");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);
        _icon.ContextMenuStrip = menu;
    }

    /// <summary>圖示目前是否可見（於 system tray）。</summary>
    public bool IsVisible => !_disposed && _icon.Visible;

    /// <summary>使用者從匣選單要求結束應用程式。</summary>
    public event Action? ExitRequested;

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