using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 應用程式進入點：顯示啟動畫面 → 1200ms → 需要時登入（M42）→ 開啟主視窗並淡出啟動畫面。
/// 任何啟動例外寫入 %TEMP%\helivms-startup.log 後重新擲出。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 診斷日誌大小上限。
    ///
    /// 崩潰記錄是無條件附加的，而有兩種狀況會讓它每秒寫一次：啟動失敗的迴圈、
    /// 或某個計時器不斷丟出同一個例外。沒有上限時 %TEMP% 會被一個純文字檔灌滿，
    /// 連帶影響錄影（磁碟寫滿時 ffmpeg 寫不出分段）。5 MB 足夠看完最近幾次崩潰。
    /// </summary>
    private const long MaxDiagnosticLogBytes = 5 * 1024 * 1024;

    /// <summary>單一實例守衛；null 表示本行程是重複啟動、正在結束自己。</summary>
    private SingleInstanceGuard? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 單一實例判斷必須在啟動畫面之前：先開 splash／登入再退出，使用者會看到閃一下的
        // 假啟動，而第二個行程早已開始搶同一顆 index.db 與同一批 ffmpeg 子行程。
        _singleInstance = SingleInstanceGuard.TryAcquire(ActivateExistingWindow);
        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        KillOrphanChildren();
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
            var path = Path.Combine(Path.GetTempPath(), "helivms-startup.log");
            File.WriteAllText(
                path,
                $"{DateTime.UtcNow:O}\n{ex}\n");
            try
            {
                MessageBox.Show(
                    $"Startup failed. See log: {path}\n\n" +
                    "Check if index.db is corrupted or paths/permissions are valid.",
                    "HeliVMS - Startup failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // 如果 MessageBox 因為無法建立視窗而失敗，就不要再重擲造成完全無訊息的卡住。
            }

            Shutdown(1);
            return;
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

        BoundedFileLog.Append(
            Path.Combine(Path.GetTempPath(), "helivms-crash.log"),
            $"{DateTime.UtcNow:O}\n{ex}\n",
            MaxDiagnosticLogBytes);
    }

    /// <summary>
    /// 登入驗證已開但找不到任何啟用中的 admin 時，寫下警告再安全開機。
    /// 與 crash log 分開，避免營運人員誤以為程式崩潰。
    /// </summary>
    private static void WriteAuthBootstrapWarning()
    {
        BoundedFileLog.Append(
            Path.Combine(Path.GetTempPath(), "helivms-auth-bootstrap.log"),
            $"{DateTime.UtcNow:O} auth.enabled=1 但沒有任何啟用中的 admin；"
            + "已略過登入以允許復原，請進設定新增 admin 或停用登入驗證。\n",
            MaxDiagnosticLogBytes);
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

        // 登入驗證已開，卻沒有任何啟用中的 admin（例如舊資料、或繞過 UI 直接改 DB）：
        // 若照常開 LoginWindow，畫面會卡在無法登入的登入框，使用者永遠進不了程式。
        // 這裡安全開機（bootstrap fail-open，以未登入＝admin 放行）並留下警告，
        // 讓操作者能進去補一個 admin；正常 UI 路徑已被 SettingsWindow 的守衛擋住。
        if (!auth.HasEnabledAdmin())
        {
            WriteAuthBootstrapWarning();
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
        call.ToolTip = Localizer.T("Osk.CallTip");
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

    /// <summary>
    /// 第二個實例按下啟動時，把既有主視窗還原到前景。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="SingleInstanceGuard"/> 的背景等待執行緒呼叫；直接碰視窗會擲跨執行緒例外，
    /// 因此一律經 <see cref="System.Windows.Threading.Dispatcher"/> 轉回 UI 執行緒。主視窗可能還沒建立
    /// （仍在啟動畫面或登入），此時 no-op。
    /// </remarks>
    private void ActivateExistingWindow()
    {
        Dispatcher.BeginInvoke(new Action(() => (this.MainWindow as MainWindow)?.BringToFront()));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstance?.Dispose();
        }
        catch
        {
            // 關閉路徑不該因釋放守衛失敗而中斷（例如互斥鎖已被系統回收）。
        }

        _singleInstance = null;

        try
        {
            KillOrphanChildren();
        }
        catch
        {
            // 關閉路徑不該因清掃子行程失敗而中斷。
        }

        base.OnExit(e);
    }

    /// <summary>
    /// 收掉前一輪執行遺留的子行程：<c>ffmpeg</c> 與 <c>ffprobe</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 啟動時掃一次是為了「上次沒有正常結束」的殘骸：程式被工作管理員砍掉、當機、斷電，
    /// 子行程都不會跟著死，它們繼續吃攝影機的 RTSP 連線與磁碟寫入，而新的行程又開一批新的，
    /// 於是每一次當機都讓負載往上疊一層。
    /// </para>
    /// <para>
    /// 結束時再掃一次是兜底：正常路徑會先請 ffmpeg 收尾（見 <c>SegmentRecorder.StopAsync</c>），
    /// 會留在這裡代表收尾逾時或流程根本沒跑到，此時硬砍是唯一的選擇。
    /// </para>
    /// <para>
    /// <c>ffprobe</c> 必須一起收：它比 ffmpeg 更隱形——健康檢查與完整性抽查會短暫啟動它，
    /// 使用者在工作管理員裡看到的正是「關掉程式之後還有一排 FFPROBE.exe」。
    /// </para>
    /// </remarks>
    private static void KillOrphanChildren()
    {
        foreach (var name in OrphanChildNames)
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    try
                    {
                        using (p)
                        {
                            if (p.Id == System.Diagnostics.Process.GetCurrentProcess().Id)
                            {
                                continue;
                            }

                            try { p.Kill(entireProcessTree: true); } catch { }
                            try { p.Kill(); } catch { }
                        }
                    }
                    catch
                    {
                        // 行程在列舉與砍殺之間自己結束：正常，繼續下一個。
                    }
                }
            }
            catch
            {
                // 列舉失敗（權限、WMI 不可用）不該影響啟動或關閉。
            }
        }
    }

    /// <summary>本程式會直接啟動的子行程名稱（不含 <c>.exe</c>）。</summary>
    private static readonly string[] OrphanChildNames = ["ffmpeg", "ffprobe"];
}
