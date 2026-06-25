// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HeliVMS.Helpers;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Dialog;

public partial class UserEditDialog : Window {
    public User? User { get; private set; }
    public string Password { get; private set; } = string.Empty;
    private readonly User? _existingUser;
    private readonly IAuthenticationService _auth;

    public UserEditDialog() : this(null) { }

    public UserEditDialog(User? existingUser) {
        InitializeComponent();
        _auth = App.Services.GetRequiredService<IAuthenticationService>();
        _existingUser = existingUser;
        if (existingUser is not null) {
            Title = "編輯用戶";
            UsernameBox.Text = existingUser.Username;
            UsernameBox.IsEnabled = false;
            PasswordBox.Tag = existingUser.Id;
            SelectDisplayNameByRole(existingUser.Role);
            LoadPermissions(existingUser.Permissions);
            AutoLoginCheckBox.IsChecked = existingUser.IsAutoLogin;
            LoadTwoFactorState(existingUser);
        }
        if (existingUser is null) {
            UpdatePermissionsSectionVisibility("Operator");
        }
        Loaded += (_, _) => {
            Activate();
            UsernameBox.Focus();
        };
    }

    private void LoadTwoFactorState(User user) {
        TwoFactorSection.Visibility = Visibility.Visible;
        EnableTwoFactorCheckBox.IsChecked = user.IsTwoFactorEnabled;
        if (user.IsTwoFactorEnabled && !string.IsNullOrEmpty(user.TwoFactorSecret)) {
            TwoFactorSecretPanel.Visibility = Visibility.Visible;
            var uri = TotpHelper.GenerateQrCodeUri(user.Username, user.TwoFactorSecret);
            TwoFactorSecretText.Text = uri;
            LoadQrCodeAsync(uri);
        }
    }

    private async void LoadQrCodeAsync(string uri) {
        try {
            var encoded = Uri.EscapeDataString(uri);
            var qrUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=160x160&data={encoded}";
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(qrUrl);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            QrCodeImage.Source = bitmap;
            QrCodeImage.Visibility = Visibility.Visible;
        } catch {
            QrCodeImage.Visibility = Visibility.Collapsed;
        }
    }

    private void CopyKeyButton_Click(object sender, RoutedEventArgs e) {
        try {
            var uri = TwoFactorSecretText.Text;
            if (!string.IsNullOrEmpty(uri)) {
                Clipboard.SetText(uri);
                CopyKeyButton.Content = "已複製";
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                t.Tick += (_, _) => { t.Stop(); CopyKeyButton.Content = "複製金鑰"; };
                t.Start();
            }
        } catch { }
    }

    private void EnableTwoFactorCheckBox_Checked(object sender, RoutedEventArgs e) {
        if (_existingUser is null) { return; }

        if (string.IsNullOrEmpty(_existingUser.TwoFactorSecret)) {
            _auth.SetupTwoFactor(_existingUser.Id);
        }

        var updated = App.Services.GetRequiredService<IUserService>().GetUserById(_existingUser.Id);
        if (updated is not null && updated.TwoFactorSecret is not null) {
            _existingUser.TwoFactorSecret = updated.TwoFactorSecret;
            _existingUser.IsTwoFactorEnabled = updated.IsTwoFactorEnabled;
            TwoFactorSecretPanel.Visibility = Visibility.Visible;
            var uri = TotpHelper.GenerateQrCodeUri(updated.Username, updated.TwoFactorSecret);
            TwoFactorSecretText.Text = uri;
            LoadQrCodeAsync(uri);
        }
    }

    private void EnableTwoFactorCheckBox_Unchecked(object sender, RoutedEventArgs e) {
        if (_existingUser is null) { return; }

        var dlg = new InputPasswordDialog("請輸入密碼以停用雙因子驗證") {
            Owner = this
        };
        if (dlg.ShowDialog() != true) return;

        var password = dlg.Password;
        if (string.IsNullOrEmpty(password)) {
            EnableTwoFactorCheckBox.IsChecked = true;
            MessageBox.Show("密碼不可為空", "驗證錯誤",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_auth.DisableTwoFactor(_existingUser.Id, password)) {
            TwoFactorSecretPanel.Visibility = Visibility.Collapsed;
            _existingUser.IsTwoFactorEnabled = false;
            _existingUser.TwoFactorSecret = null;
        } else {
            EnableTwoFactorCheckBox.IsChecked = true;
            MessageBox.Show("密碼驗證失敗，無法停用雙因子驗證", "驗證錯誤",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DisplayNameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (RoleCombo is null) { return; }
        if (DisplayNameCombo.SelectedItem is ComboBoxItem src && src.Tag is string tag) {
            foreach (ComboBoxItem dst in RoleCombo.Items) {
                if (dst.Tag is string dstTag && dstTag == tag) {
                    dst.IsSelected = true;
                    UpdatePermissionsSectionVisibility(tag);
                    return;
                }
            }
        }
    }

    private void SelectDisplayNameByRole(UserRole role) {
        var roleStr = role.ToString();
        foreach (ComboBoxItem item in DisplayNameCombo.Items) {
            if (item.Tag is string tag && tag == roleStr) {
                item.IsSelected = true;
                return;
            }
        }
    }

    private void UpdatePermissionsSectionVisibility(string roleTag) {
        PermissionsSection.Visibility = roleTag == "Admin" ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) {
        var username = UsernameBox.Text.Trim();
        var displayName = DisplayNameCombo.SelectedItem is ComboBoxItem dn
            ? dn.Content?.ToString() ?? username
            : username;
        var password = PasswordBox.Password;
        var isEditMode = PasswordBox.Tag is not null;
        var isAutoLogin = AutoLoginCheckBox.IsChecked == true;

        if (string.IsNullOrEmpty(username)) {
            MessageBox.Show("請填寫帳號", "驗證錯誤",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            UsernameBox.Focus();
            return;
        }

        if (!isEditMode && string.IsNullOrEmpty(password)) {
            MessageBox.Show("請填寫密碼", "驗證錯誤",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PasswordBox.Focus();
            return;
        }

        var role = UserRole.Operator;
        if (DisplayNameCombo.SelectedItem is ComboBoxItem dnItem && dnItem.Tag is string dnTag) {
            role = dnTag switch {
                "Admin" => UserRole.Admin,
                "Viewer" => UserRole.Viewer,
                _ => UserRole.Operator
            };
        }

        var userPermissions = role == UserRole.Admin
            ? []
            : CollectPermissions();

        if (isEditMode && _existingUser is not null) {
            User = new User {
                Id = _existingUser.Id,
                Username = username,
                PasswordHash = string.IsNullOrEmpty(password) ? _existingUser.PasswordHash : "",
                DisplayName = displayName,
                Role = role,
                IsAutoLogin = isAutoLogin,
                IsEnabled = _existingUser.IsEnabled,
                CreatedAt = _existingUser.CreatedAt,
                IsTwoFactorEnabled = EnableTwoFactorCheckBox.IsChecked == true,
                TwoFactorSecret = _existingUser.TwoFactorSecret,
                Permissions = userPermissions
            };
            Password = password;
        } else {
            User = new User {
                Username = username,
                DisplayName = displayName,
                Role = role,
                IsAutoLogin = isAutoLogin,
                Permissions = userPermissions
            };
            Password = password;
        }

        DialogResult = true;
        Close();
    }

    private static readonly Dictionary<string, string> PermissionCheckBoxes = new() {
        ["PermDeviceMgmt"] = "DeviceManagement",
        ["PermPlayback"] = "Playback",
        ["PermSystemSettings"] = "SystemSettings",
        ["PermLicense"] = "License",
        ["PermPTZ"] = "PTZControl",
    };

    private void LoadPermissions(List<string> permissions) {
        foreach (var (fieldName, permName) in PermissionCheckBoxes) {
            var field = FindName(fieldName) as CheckBox;
            field?.IsChecked = permissions.Contains(permName);
        }
    }

    private List<string> CollectPermissions() {
        var result = new List<string>(PermissionCheckBoxes.Count);
        foreach (var (fieldName, permName) in PermissionCheckBoxes) {
            if (FindName(fieldName) is CheckBox cb && cb.IsChecked == true) {
                result.Add(permName);
            }
        }
        return result;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }
}
