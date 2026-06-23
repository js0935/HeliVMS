using System.Windows.Controls;

namespace HeliVMS.Services;

public enum NavPage {
    Dashboard,
    Login,
    LiveView,
    DeviceManagement,
    Playback,
    UserManagement,
    Settings,
    License,
    EMap
}

public interface INavigationService {
    UserControl? CurrentPage { get; }
    event Action? PageChanged;
    void NavigateTo(NavPage page);
    bool CanNavigate { get; }
    void Initialize();
}
