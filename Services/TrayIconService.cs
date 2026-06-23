using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace HeliVMS.Services;

public sealed class TrayIconService : IDisposable {
    private TaskbarIcon? _trayIcon;
    private Window? _mainWindow;

    public void Initialize(Window mainWindow) {
        _mainWindow = mainWindow;

        var iconStream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/images/HeliNVR.ico"))?.Stream;
        if (iconStream is null) {
            Serilog.Log.Warning("[Tray] Icon resource not found, tray icon disabled");
            return;
        }

        _trayIcon = new TaskbarIcon {
            Icon = new System.Drawing.Icon(iconStream),
            ToolTipText = "HeliVMS — 智慧影像管理系統",
        };

        var showItem = new System.Windows.Controls.MenuItem { Header = "顯示主視窗" };
        showItem.Click += (_, _) => {
            _mainWindow?.Show();
            _mainWindow?.Activate();
        };

        var hideItem = new System.Windows.Controls.MenuItem { Header = "隱藏主視窗" };
        hideItem.Click += (_, _) => {
            _mainWindow?.Hide();
        };

        var exitItem = new System.Windows.Controls.MenuItem { Header = "結束" };
        exitItem.Click += (_, _) => {
            Serilog.Log.Information("[Tray] Exit requested via tray menu");
            if (_trayIcon is not null) _trayIcon.Visibility = Visibility.Collapsed;
            System.Windows.Application.Current.Shutdown();
        };

        var contextMenu = new System.Windows.Controls.ContextMenu();
        contextMenu.Items.Add(showItem);
        contextMenu.Items.Add(hideItem);
        contextMenu.Items.Add(new System.Windows.Controls.Separator());
        contextMenu.Items.Add(exitItem);
        _trayIcon.ContextMenu = contextMenu;

        _trayIcon.TrayMouseDoubleClick += (_, _) => {
            _mainWindow?.Show();
            _mainWindow?.Activate();
        };

        _mainWindow.Closing += (_, e) => {
            e.Cancel = true;
            _mainWindow?.Hide();
            Serilog.Log.Information("[Tray] Window minimized to tray on close");
        };

        Serilog.Log.Information("[Tray] Tray icon initialized");
    }

    public void ShowBalloonTip(string title, string text) {
        _trayIcon?.ShowBalloonTip(title, text, BalloonIcon.Info);
    }

    public void Dispose() {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
