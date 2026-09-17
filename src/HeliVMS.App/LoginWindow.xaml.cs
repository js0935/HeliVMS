using System.Windows;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 登入視窗（M42，§18.6）：使用者名稱／密碼／錯誤訊息。
/// 成功時設定 <see cref="SessionContext"/> 並回傳 DialogResult=true。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly AuthService _auth;

    public LoginWindow(AuthService auth)
    {
        _auth = auth;
        InitializeComponent();
        Loaded += (_, _) => UsernameInput.Focus();
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

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}