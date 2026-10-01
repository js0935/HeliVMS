using System.Windows;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 登入視窗（M42，§18.6）：使用者名稱／密碼／錯誤訊息；M50 追加企業 SSO（OIDC 權杖登入）；
/// M86 追加 LDAP／AD 綁定登入（真實 wire LDAP client）。
/// 成功時設定 <see cref="SessionContext"/> 並回傳 DialogResult=true。
/// </summary>
public partial class LoginWindow : Window
{
    private const int BaseHeight = 300;
    private const int OidcPanelHeight = 170;
    private const int LdapPanelHeight = 140;
    private readonly AuthService _auth;
    private readonly EnterpriseAuthService? _enterprise;
    private readonly SqliteStore? _enterpriseStore;

    public LoginWindow(AuthService auth)
        : this(auth, null)
    {
    }

    public LoginWindow(AuthService auth, SqliteStore? store)
    {
        _auth = auth;
        _enterpriseStore = store;
        InitializeComponent();

        if (store is not null)
        {
            _enterprise = new EnterpriseAuthService(store);
            LoadEnterpriseProviders();
        }

        Height = BaseHeight;
        Loaded += (_, _) => UsernameInput.Focus();
    }

    /// <summary>ad 旗標的拒絕訊息；null 表示可放行（含未提供 store 的純本機登入模式）。</summary>
    private string? EnterpriseDenial()
    {
        if (_enterpriseStore is null)
        {
            return null;
        }

        var state = new LicenseService(_enterpriseStore).Evaluate(DateTime.UtcNow);
        return state.AllowsFeature(LicenseFeatures.AdSso)
            ? null
            : state.FeatureDenialMessage(LicenseFeatures.AdSso) ?? "目前授權未包含企業登入。";
    }

    private void LoadEnterpriseProviders()
    {
        if (_enterprise is null)
        {
            return;
        }

        // ad 旗標限定（M209）：未授權時完全不顯示企業 SSO 面板。
        // 高度計算一併跳過，否則會留下一塊空白。
        if (EnterpriseDenial() is not null)
        {
            return;
        }

        var oidc = _enterprise.ListEnabledOidc();
        if (oidc.Count > 0)
        {
            OidcProviderCombo.ItemsSource = oidc;
            OidcProviderCombo.SelectedIndex = 0;
            OidcPanel.Visibility = Visibility.Visible;
            Height = BaseHeight + OidcPanelHeight;
        }

        var ldap = _enterprise.ListEnabledLdap();
        if (ldap.Count > 0)
        {
            LdapProviderCombo.ItemsSource = ldap;
            LdapProviderCombo.SelectedIndex = 0;
            LdapPanel.Visibility = Visibility.Visible;
            Height = BaseHeight + (OidcPanel.Visibility == Visibility.Visible ? OidcPanelHeight : 0) + LdapPanelHeight;
        }
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
        // ad 旗標限定（M219）：面板藏了還得擋 handler（鍵盤／程式化點擊繞得過）。
        if (EnterpriseDenial() is { } oidcDenial)
        {
            OidcMessage.Text = oidcDenial;
            return;
        }

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

    private void OnLdapLoginClicked(object sender, RoutedEventArgs e)
    {
        // ad 旗標限定（M219）：面板藏了還得擋 handler（鍵盤／程式化點擊繞得過）。
        if (EnterpriseDenial() is { } ldapDenial)
        {
            LdapMessage.Text = ldapDenial;
            return;
        }

        if (_enterprise is null || LdapProviderCombo.SelectedItem is not AuthProviderRecord provider)
        {
            LdapMessage.Text = "未選擇 LDAP 提供者";
            return;
        }

        var username = UsernameInput.Text.Trim();
        if (username.Length == 0)
        {
            LdapMessage.Text = "請輸入使用者名稱";
            return;
        }

        var password = PasswordInput.Password;
        if (password.Length == 0)
        {
            LdapMessage.Text = "請輸入密碼";
            return;
        }

        LdapLoginButton.IsEnabled = false;
        try
        {
            var result = _enterprise.AuthenticateLdap(provider, username, password, new LdapClient());
            if (result.Succeeded)
            {
                SessionContext.CurrentUser = new SessionUser(username, result.Role, result.DisplayName);
                DialogResult = true;
                return;
            }

            LdapMessage.Text = result.Error;
            PasswordInput.Clear();
            PasswordInput.Focus();
        }
        finally
        {
            LdapLoginButton.IsEnabled = true;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
