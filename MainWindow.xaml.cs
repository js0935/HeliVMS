using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HeliVMS.Services;
using Serilog;
using HeliVMS.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS;

public partial class MainWindow : Window
{
    private readonly INavigationService _nav;
    private readonly IAuthenticationService _auth;
    private readonly IAuthorizationService _authz;
    private readonly ISystemStatusService _status;
    private bool _initialized;

    public MainWindow()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        InitializeComponent();
        Log.Information("[TIMING] MainWindow: InitializeComponent: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        _nav = App.Services.GetRequiredService<INavigationService>();
        Log.Information("[TIMING] MainWindow: Nav resolved: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        _auth = App.Services.GetRequiredService<IAuthenticationService>();
        Log.Information("[TIMING] MainWindow: Auth resolved: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        _authz = App.Services.GetRequiredService<IAuthorizationService>();
        Log.Information("[TIMING] MainWindow: Authz resolved: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        _status = App.Services.GetRequiredService<ISystemStatusService>();
        Log.Information("[TIMING] MainWindow: Status resolved: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        _nav.PageChanged += OnPageChanged;
        _auth.LoginSucceeded += OnLoginSucceeded;
        _auth.LoggedOut += OnLoggedOut;
        _status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ISystemStatusService.StatusSummary))
            {
                _ = Dispatcher.InvokeAsync(() => StatusBarText.Text = _status.StatusSummary);
            }
        };

        var notif = App.Services.GetRequiredService<INotificationService>();
        notif.NotificationReceived += args =>
        {
            _ = Dispatcher.InvokeAsync(() => ShowToast(args.Message, args.Severity));
        };

        // Session timeout: auto logout
        _auth.SessionExpired += () =>
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                _auth.Logout();
                ShowToast("閒置超過 30 分鐘，已自動登出", "WARN");
            });
        };

        // Global input listener: reset session timeout timer
        EventManager.RegisterClassHandler(typeof(UIElement),
            UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => _auth.ResetSessionTimer()));
        EventManager.RegisterClassHandler(typeof(UIElement),
            UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => _auth.ResetSessionTimer()));

        Closing += MainWindow_Closing;

        _status.StartMonitoring();
        Log.Information("[TIMING] MainWindow: StatusMonitor: {ElapsedMs}ms", sw.ElapsedMilliseconds);

        // Deferred init: App.Services is ready, pages can safely use DI
        _nav.Initialize();
        Log.Information("[TIMING] MainWindow: _nav.Initialize (LoginView): {ElapsedMs}ms", sw.ElapsedMilliseconds);

        Sidebar.Visibility = Visibility.Collapsed;

        _initialized = true;
    }

    /// <summary>Sidebar navigation — switch page on RadioButton selection change</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized) { return; }

        if (sender is not RadioButton rb) { return; }
        if (rb.Tag is not string tag) { return; }
        if (!Enum.TryParse<NavPage>(tag, out var page)) { return; }

        if (page != NavPage.Login && !_auth.IsLoggedIn)
        {
            Log.Debug("[HeliVMS] Nav blocked ({Page}): not logged in", page);
            return;
        }

        // Permission check (non-LiveView/Login pages require permission)
        var requiredPerm = page switch
        {
            NavPage.DeviceManagement => Permission.DeviceManagement,
            NavPage.Playback => Permission.Playback,
            NavPage.Settings => Permission.SystemSettings,
            NavPage.License => Permission.License,
            _ => (Permission?)null,
        };
        if (requiredPerm.HasValue && !_authz.HasPermission(requiredPerm.Value))
        {
            Log.Debug("[HeliVMS] Nav blocked ({Page}): no {Perm} permission", page, requiredPerm.Value);
            return;
        }

        try
        {
            _nav.NavigateTo(page);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[HeliVMS] NavigateTo({Page}) failed", page);
            MessageBox.Show($"無法開啟頁面：{ex.Message}", "導航錯誤",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPageChanged()
    {
        Log.Debug("[HeliVMS] OnPageChanged: CurrentPage={Page}", _nav.CurrentPage?.GetType().Name);
        if (_nav.CurrentPage is not null)
        {
            MainContent.Content = _nav.CurrentPage;
        }
    }

    private void OnLoginSucceeded(Models.User user)
    {
        SyncSidebarAfterLogin(user);
    }

    private void SyncSidebarAfterLogin(Models.User user)
    {
        Sidebar.Visibility = Visibility.Visible;
        UserDisplayName.Text = user.DisplayName;
        PowerButton.Visibility = Visibility.Visible;

        bool Has(Permission p) => _authz.HasPermission(p);

        NavDashboard.Visibility = Visibility.Visible;
        NavLiveView.Visibility = Visibility.Visible;
        NavDeviceMgmt.Visibility = Has(Permission.DeviceManagement) ? Visibility.Visible : Visibility.Collapsed;
        NavPlayback.Visibility = Has(Permission.Playback) ? Visibility.Visible : Visibility.Collapsed;

        var hasSystemAccess = Has(Permission.SystemSettings) || Has(Permission.License);
        SectionSystemMgmt.Visibility = hasSystemAccess ? Visibility.Visible : Visibility.Collapsed;
        NavSettings.Visibility = Has(Permission.SystemSettings) ? Visibility.Visible : Visibility.Collapsed;
        NavLicense.Visibility = Has(Permission.License) ? Visibility.Visible : Visibility.Collapsed;

        NavDashboard.IsChecked = true;
        _nav.NavigateTo(NavPage.Dashboard);
    }

    private void OnLoggedOut()
    {
        Sidebar.Visibility = Visibility.Collapsed;
        UserDisplayName.Text = "未登入";
        PowerButton.Visibility = Visibility.Collapsed;
        NavLiveView.IsChecked = false;
    }

    private void PowerButton_Click(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = !PowerMenuPopup.IsOpen;
    }

    private async void ExitApp_Click(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        await ShutdownApplicationAsync();
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // When triggered by close button or Alt+F4, show shutdown screen first
        if (_isShuttingDown) { return; }
        e.Cancel = true;
        await ShutdownApplicationAsync();
    }

    private bool _isShuttingDown;

    private async Task ShutdownApplicationAsync()
    {
        if (_isShuttingDown) { return; }
        _isShuttingDown = true;

        var shutdownWindow = new ShutdownWindow();
        shutdownWindow.Show();

        try
        {
            var scheduler = App.Services.GetRequiredService<RecordingSchedulerService>();
            var stopTask = Task.Run(() =>
                scheduler.Stop(msg => shutdownWindow.AppendStatus(msg)));
            if (await Task.WhenAny(stopTask, Task.Delay(6000)) != stopTask)
            {
                shutdownWindow.AppendStatus("停止錄影逾時，強制終止殘留 ffmpeg…");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[HeliVMS] ShutdownApplicationAsync error");
        }
        finally
        {
            RecordingService.KillAllFfmpegProcesses();
        }

        shutdownWindow.Close();
        Application.Current.Shutdown();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        var win = Window.GetWindow(this);
        if (win is not null)
        {
            win.WindowState = WindowState.Minimized;
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        _auth.Logout();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        PowerMenuPopup.IsOpen = false;
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionStr = version is not null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        MessageBox.Show(
            $"HeliVMS — 智慧影像管理系統\n\n版本：{versionStr}\n\n禾秝軟體開發團隊",
            "關於 HeliVMS",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private DispatcherTimer? _toastTimer;

    private void ShowToast(string message, string severity)
    {
        ToastText.Text = $"[{severity}] {message}";
        ToastOverlay.Visibility = Visibility.Visible;
        if (_toastTimer is null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer?.Stop();
                ToastOverlay.Visibility = Visibility.Collapsed;
            };
        }
        else
        {
            _toastTimer.Stop();
        }
        _toastTimer.Start();
    }
}
