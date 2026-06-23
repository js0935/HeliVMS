// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace HeliVMS.Views;

public partial class LoginView : UserControl {
    private readonly IAuthenticationService _auth;
    private readonly IUserService _userService;

    public LoginView() {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        InitializeComponent();
        var initTime = sw.ElapsedMilliseconds;
        _auth = App.Services.GetRequiredService<IAuthenticationService>();
        var authTime = sw.ElapsedMilliseconds;
        _userService = App.Services.GetRequiredService<IUserService>();
        var userTime = sw.ElapsedMilliseconds;
        Log.Information("[TIMING] LoginView: InitComponent={InitMs}ms, Auth={AuthMs}ms, User={UserMs}ms",
            initTime, authTime - initTime, userTime - authTime);
        Loaded += (_, _) => RestoreLoginConfig();
    }

    private void RestoreLoginConfig() {
        var config = LoginConfig.Load();
        if (config is null) {
            UsernameBox.Focus();
            return;
        }

        if (config.RememberPassword) {
            UsernameBox.Text = config.Username;
            PasswordBox.Password = LoginConfig.Deobfuscate(config.PasswordObfuscated);
            RememberPasswordCheckBox.IsChecked = true;
        }

        // Auto-login: if password is remembered and user has IsAutoLogin=true
        var allUsers = _userService.GetAllUsers();
        User? autoLoginUser = null;
        for (var ui = 0; ui < allUsers.Count; ui++) {
            if (allUsers[ui].IsAutoLogin) { autoLoginUser = allUsers[ui]; break; }
        }
        if (config.RememberPassword && autoLoginUser is not null &&
            string.Equals(config.Username, autoLoginUser.Username, StringComparison.OrdinalIgnoreCase)) {
            TryLogin();
        } else {
            UsernameBox.Focus();
        }
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e) {
        TryLogin();
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) { TryLogin(); }
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e) {
        ClearError();
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) {
        ClearError();
    }

    private void ClearError() {
        ErrorBorder.Visibility = Visibility.Collapsed;
        ErrorText.Text = "";
    }

    private void TryLogin() {
        if (LoadingSpinner.Visibility == Visibility.Visible)
            return;

        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;

        if (string.IsNullOrEmpty(username)) {
            ShowError("請輸入帳號");
            UsernameBox.Focus();
            return;
        }
        if (string.IsNullOrEmpty(password)) {
            ShowError("請輸入密碼");
            PasswordBox.Focus();
            return;
        }

        LoadingSpinner.Visibility = Visibility.Visible;
        LoginButton.IsEnabled = false;

        try {
            // Account lockout check
            if (_auth.IsAccountLocked(username)) {
                var remain = _auth.GetRemainingLockoutMinutes(username);
                ShowError($"帳號已鎖定，請 {remain} 分鐘後再試");
                LoadingSpinner.Visibility = Visibility.Collapsed;
                LoginButton.IsEnabled = true;
                return;
            }

            var (Success, RequiresTwoFactor) = _auth.LoginWithTwoFactorSupport(username, password);
            if (Success && RequiresTwoFactor) {
                ClearError();
                ShowTwoFactorPanel();
            } else if (Success) {
                ClearError();
                SaveLoginConfig(username, password);
            } else {
                if (_auth.IsAccountLocked(username)) {
                    var remain = _auth.GetRemainingLockoutMinutes(username);
                    ShowError($"密碼錯誤次數過多，帳號已鎖定 {remain} 分鐘");
                } else {
                    ShowError("帳號或密碼錯誤");
                }
                PasswordBox.Clear();
                PasswordBox.Focus();
            }
        } catch (Exception ex) {
            ShowError($"登入失敗：{ex.Message}");
        } finally {
            LoadingSpinner.Visibility = Visibility.Collapsed;
            LoginButton.IsEnabled = true;
        }
    }

    private void ShowTwoFactorPanel() {
        LoginButton.Visibility = Visibility.Collapsed;
        RememberPasswordCheckBox.Visibility = Visibility.Collapsed;
        TwoFactorPanel.Visibility = Visibility.Visible;
        TwoFactorCodeBox.Focus();
    }

    private void HideTwoFactorPanel() {
        TwoFactorPanel.Visibility = Visibility.Collapsed;
        LoginButton.Visibility = Visibility.Visible;
        RememberPasswordCheckBox.Visibility = Visibility.Visible;
        TwoFactorCodeBox.Clear();
    }

    private void VerifyTwoFactorButton_Click(object sender, RoutedEventArgs e) {
        TryVerifyTwoFactor();
    }

    private void TwoFactorCodeBox_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) { TryVerifyTwoFactor(); }
    }

    private void BackToLoginButton_Click(object sender, RoutedEventArgs e) {
        HideTwoFactorPanel();
        ClearError();
        PasswordBox.Focus();
    }

    private void TryVerifyTwoFactor() {
        var code = TwoFactorCodeBox.Text.Trim();
        if (string.IsNullOrEmpty(code) || code.Length != 6) {
            ShowError("請輸入 6 位數驗證碼");
            TwoFactorCodeBox.Focus();
            return;
        }

        LoadingSpinner.Visibility = Visibility.Visible;
        VerifyTwoFactorButton.IsEnabled = false;

        try {
            if (_auth.CompleteTwoFactorLogin(code)) {
                ClearError();
                var username = UsernameBox.Text.Trim();
                var password = PasswordBox.Password;
                SaveLoginConfig(username, password);
            } else {
                ShowError("驗證碼錯誤，請重試");
                TwoFactorCodeBox.Clear();
                TwoFactorCodeBox.Focus();
            }
        } catch (Exception ex) {
            ShowError($"驗證失敗：{ex.Message}");
        } finally {
            LoadingSpinner.Visibility = Visibility.Collapsed;
            VerifyTwoFactorButton.IsEnabled = true;
        }
    }

    private void SaveLoginConfig(string username, string password) {
        var remember = RememberPasswordCheckBox.IsChecked == true;

        if (!remember) {
            LoginConfig.Clear();
            return;
        }

        // Auto-login determined by user's IsAutoLogin, not by checkbox
        var user = _userService.GetUserByUsername(username);

        var config = new LoginConfig {
            Username = username,
            PasswordObfuscated = LoginConfig.Obfuscate(password),
            RememberPassword = true,
            AutoLogin = user?.IsAutoLogin ?? false
        };
        config.Save();
    }

    private void ShowError(string message) {
        ErrorText.Text = message;
        ErrorBorder.Visibility = Visibility.Visible;

        // Shake animation on login button (HeliNVR style)
        try {
            LoginButton.BeginAnimation(Button.RenderTransformProperty, null!);
            LoginButton.RenderTransform = new TranslateTransform();
            var anim = new DoubleAnimation(-6, 6, new Duration(TimeSpan.FromMilliseconds(50))) {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3)
            };
            LoginButton.RenderTransform.BeginAnimation(
                TranslateTransform.XProperty, anim);
        } catch {
            // Animation failure does not affect functionality
        }
    }
}
