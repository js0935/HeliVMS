// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Windows.Controls;
using HeliVMS.Views;

namespace HeliVMS.Services;

public class NavigationService : INavigationService
{
    private readonly IAuthenticationService _auth;
    private readonly Dictionary<NavPage, Func<UserControl>> _pageFactories;
    private readonly Dictionary<NavPage, UserControl> _pageCache = new();

    public UserControl? CurrentPage { get; private set; }
    public event Action? PageChanged;
    public bool CanNavigate => _auth.IsLoggedIn;

    private static bool ShouldCache(NavPage page) => page switch
    {
        NavPage.LiveView => true,
        NavPage.Dashboard => true,
        _ => false,
    };

    public NavigationService(IAuthenticationService auth)
    {
        _auth = auth;
        _pageFactories = new()
        {
            [NavPage.Dashboard] = () => new DashboardView(),
            [NavPage.Login] = () => new LoginView(),
            [NavPage.LiveView] = () => new LiveView(),
            [NavPage.DeviceManagement] = () => new DeviceManagementView(),
            [NavPage.UserManagement] = () => new UserManagementView(),
            [NavPage.Playback] = () => new PlaybackView(),
            [NavPage.Settings] = () => new SettingsView(),
            [NavPage.License] = () => new LicenseView(),
        };

        _auth.LoginSucceeded += _ => NavigateTo(NavPage.LiveView);
        _auth.LoggedOut += () => NavigateTo(NavPage.Login);
    }

    /// <summary>Lazy initialization — called by MainWindow after App.Services is ready</summary>
    public void Initialize()
    {
        if (CurrentPage is null)
        {
            NavigateTo(NavPage.Login);
        }
    }

    public void NavigateTo(NavPage page)
    {
        Serilog.Log.Information("NavigateTo({Page}), IsLoggedIn={IsLoggedIn}", page, _auth.IsLoggedIn);

        if (page != NavPage.Login && !_auth.IsLoggedIn)
        {
            Serilog.Log.Warning("NavigateTo blocked: not logged in");
            return;
        }

        if (_pageFactories.TryGetValue(page, out var factory))
        {
            try
            {
                if (!_pageCache.TryGetValue(page, out var cached))
                {
                    cached = factory();
                    if (ShouldCache(page))
                    {
                        _pageCache[page] = cached;
                    }
                }

                CurrentPage = cached;
                Serilog.Log.Information("NavigateTo: using {Type} (cached={Cached})",
                    CurrentPage?.GetType().Name, _pageCache.TryGetValue(page, out _));
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "NavigateTo: failed to create page for {Page}", page);
                System.Windows.MessageBox.Show(
                    $"無法建立頁面「{page}」：{ex.GetType().Name}\n{ex.Message}",
                    "導航錯誤", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                CurrentPage = null;
                return;
            }

            try
            {
                PageChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "NavigateTo: PageChanged handler threw");
            }
        }
        else
        {
            Serilog.Log.Warning("NavigateTo: no factory for {Page}", page);
        }
    }
}
