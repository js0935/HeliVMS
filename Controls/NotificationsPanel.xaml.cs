using System;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.Controls;

public partial class NotificationsPanel : UserControl {
    private readonly NotificationHistoryService _notif = App.Services.GetRequiredService<NotificationHistoryService>();

    public event EventHandler? CloseRequested;

    public NotificationsPanel() {
        InitializeComponent();
        NotifList.ItemsSource = _notif.Entries;
        _notif.Updated += () => Dispatcher.InvokeAsync(() => NotifList.Items.Refresh());
    }

    public void Refresh() => NotifList.Items.Refresh();

    private void MarkAllRead_Click(object sender, RoutedEventArgs e) {
        _notif.MarkAllAsRead();
        NotifList.Items.Refresh();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e) {
        _notif.Clear();
        NotifList.Items.Refresh();
    }

    private void ClosePanel_Click(object sender, RoutedEventArgs e) {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
