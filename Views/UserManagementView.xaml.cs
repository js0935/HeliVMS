using System.Windows;
using System.Windows.Controls;
using HeliVMS.Dialog;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Views;

public partial class UserManagementView : UserControl {
    private readonly IUserService _userService;

    public UserManagementView() {
        InitializeComponent();
        _userService = App.Services.GetRequiredService<IUserService>();
        Loaded += (_, _) => RefreshList();
    }

    private void RefreshList() {
        var users = _userService.GetAllUsers();
        UserGrid.ItemsSource = users;
        UpdateStatistics(users);
    }

    private void UpdateStatistics(System.Collections.Generic.List<User> users) {
        UserCountText.Text = users.Count.ToString();
        var enabledCount = 0;
        for (var i = 0; i < users.Count; i++) {
            if (users[i].IsEnabled) enabledCount++;
        }
        EnabledUserCountText.Text = enabledCount.ToString();
    }

    private void AddUserButton_Click(object sender, RoutedEventArgs e) {
        var dlg = new UserEditDialog {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true && dlg.User is not null) {
            _userService.CreateUser(dlg.User, dlg.Password);
            RefreshList();
        }
    }

    private void EditUserButton_Click(object sender, RoutedEventArgs e) {
        if (UserGrid.SelectedItem is not User user) return;
        var dlg = new UserEditDialog(user) {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true && dlg.User is not null) {
            // Hash new password if provided, otherwise keep existing hash
            if (!string.IsNullOrEmpty(dlg.Password)) {
                dlg.User.PasswordHash = AuthenticationService.HashPasswordPBKDF2(dlg.Password);
            }
            _userService.UpdateUser(dlg.User);
            RefreshList();
        }
    }

    private void DeleteUserButton_Click(object sender, RoutedEventArgs e) {
        if (UserGrid.SelectedItem is not User user) return;
        if (string.Equals(user.Username, "admin", StringComparison.OrdinalIgnoreCase)) {
            MessageBox.Show("系統管理員帳號不可刪除。", "禁止操作",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"確定刪除用戶「{user.DisplayName}」？\n此操作無法復原。",
                "確認刪除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _userService.DeleteUser(user.Id);
        RefreshList();
    }

    private void ToggleUserButton_Click(object sender, RoutedEventArgs e) {
        if (UserGrid.SelectedItem is not User user) return;
        if (string.Equals(user.Username, "admin", StringComparison.OrdinalIgnoreCase)) {
            MessageBox.Show("系統管理員帳號不可停用。", "禁止操作",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        user.IsEnabled = !user.IsEnabled;
        _userService.UpdateUser(user);
        RefreshList();
    }

    private void UserGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        var hasSelection = UserGrid.SelectedItem is User;
        var isAdmin = UserGrid.SelectedItem is User u &&
                      string.Equals(u.Username, "admin", StringComparison.OrdinalIgnoreCase);
        EditUserButton.IsEnabled = hasSelection;
        DeleteUserButton.IsEnabled = hasSelection && !isAdmin;
        ToggleUserButton.IsEnabled = hasSelection && !isAdmin;

        if (UserGrid.SelectedItem is User selected) {
            ToggleUserButton.Content = selected.IsEnabled ? "停用" : "啟用";
        }
    }
}
