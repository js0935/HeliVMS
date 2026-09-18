using System.Windows;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 登入視窗（M42，§18.6）：使用者名稱／密碼／錯誤訊息；M50 追加企業 SSO（OIDC 權杖登入）。
/// 成功時設定 <see cref="SessionContext"/> 並回傳 DialogResult=true。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly AuthService _auth;
    private readonly EnterpriseAuthService? _enterprise;

    public LoginWindow(AuthService auth)
        : this(auth, null)
    {
    }

    public LoginWindow(AuthService auth, SqliteStore? store)
    {
        _auth = auth;
        InitializeComponent();

        if (store is not null)
        {
            _enterprise = new EnterpriseAuthService(store);
            LoadOidcProviders();
        }

        Loaded += (_, _) => UsernameInput.Focus();
    }

    private void LoadOidcProviders()
    {
        if (_enterprise is null)
        {
            return;
        }

        var providers = _enterprise.ListEnabledOidc();
        if (providers.Count == 0)
        {
            return;
        }

        OidcProviderCombo.ItemsSource = providers;
        OidcProviderCombo.SelectedIndex = 0;
        OidcPanel.Visibility = Visibility.Visible;
        Height = 470;
    }

    private void OnLoginClicked(object sender, RoutedEventArgs e)
    {
        var username = UsernameInput.Text.Trim();
        if (username.Length == 0)
        {
            LoginMessage.Text = "請輸入使用者名稱";
            return;
        }

        var result = _auth.Authenticate(username, PasswordInput.Password);
        if (result.Succeeded)
        {
            SessionContext.CurrentUser = new SessionUser(username, result.Role, result.DisplayName);
            DialogResult = true;
            return;
        }

        LoginMessage.Text = result.Error;
        PasswordInput.Clear();
        PasswordInput.Focus();
    }

    private void OnOidcLoginClicked(object sender, RoutedEventArgs e)
    {
        if (_enterprise is null || OidcProviderCombo.SelectedItem is not AuthProviderRecord provider)
        {
            OidcMessage.Text = "未選擇企業身份提供者";
            return;
        }

        var token = OidcTokenBox.Text.Trim();
        if (token.Length == 0)
        {
            OidcMessage.Text = "請貼上 ID Token";
            return;
        }

        var result = _enterprise.AuthenticateOidc(provider, token);
        if (result.Ok)
        {
            SessionContext.CurrentUser = new SessionUser(result.Username, result.Role, result.DisplayName);
            DialogResult = true;
            return;
        }

        OidcMessage.Text = result.Error;
        OidcTokenBox.SelectAll();
        OidcTokenBox.Focus();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
