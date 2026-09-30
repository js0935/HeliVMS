using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 應用程式進入點：顯示啟動畫面 → 1200ms → 需要時登入（M42）→ 開啟主視窗並淡出啟動畫面。
/// 任何啟動例外寫入 %TEMP%\helivms-startup.log 後重新擲出。
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash(args.ExceptionObject as Exception);

        try
        {
            var splash = new SplashWindow();
            splash.Show();

            await Task.Delay(1200);

            if (!PerformLogin())
            {
                Shutdown(1);
                return;
            }

            var main = new MainWindow();
            main.Show();
            await splash.FadeOutAsync();
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "helivms-startup.log"),
                $"{DateTime.UtcNow:O}\n{ex}\n");
            throw;
        }
    }

    /// <summary>
    /// 依 <c>auth.enabled</c> 決定是否要求登入（M42）。未啟用時視為 admin、直接放行。
    /// </summary>
    /// <summary>執行期中未處理例外寫入 %TEMP%\helivms-crash.log（已處理，避免整份應用閃退）。</summary>
    private static void WriteCrash(Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        File.AppendAllText(
            Path.Combine(Path.GetTempPath(), "helivms-crash.log"),
            $"{DateTime.UtcNow:O}\n{ex}\n");
    }

    private static bool PerformLogin()
    {
        var dbPath = Path.Combine(HeliVMS.App.MainWindow.ResolveDataRoot(), "index.db");
        using var store = new SqliteStore(dbPath);
        store.Initialize();

        var auth = new AuthService(store);
        if (!auth.IsAuthEnabled)
        {
            return true;
        }

        return new LoginWindow(auth, store).ShowDialog() == true;
    }

    /// <summary>全域輸入框載入：掛接右側「螢幕虛擬鍵盤」呼叫按鈕（M163）。</summary>
    private void OnInputLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.Template?.FindName("PART_OskCall", c) is not Button call)
        {
            return;
        }

        call.Tag = c;
        call.Click += OnOskCallClicked;
        Osk.RegisterCallButton(call);
    }

    private void OnInputUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.Template?.FindName("PART_OskCall", c) is not Button call)
        {
            return;
        }

        call.Click -= OnOskCallClicked;
        Osk.UnregisterCallButton(call);
    }

    private void OnOskCallClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Control target })
        {
            return;
        }

        switch (target)
        {
            case TextBox tb: Osk.Toggle(tb); break;
            case PasswordBox pb: Osk.Toggle(pb); break;
        }
    }
}