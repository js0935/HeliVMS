using System.Windows;

namespace HeliVMS.Dialog;

public partial class OnvifScanDialog : Window {
    public string Subnet => SubnetBox.Text.Trim();
    public int Port => int.TryParse(PortBox.Text, out var p) ? p : 80;
    public string Username => UsernameBox.Text.Trim();
    public string Password => PasswordBox.Password;

    public OnvifScanDialog() {
        InitializeComponent();
        Loaded += (_, _) => {
            Activate();
            SubnetBox.Focus();
        };
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e) {
        if (string.IsNullOrWhiteSpace(Subnet)) {
            MessageBox.Show("請輸入子網路前綴", "驗證錯誤",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SubnetBox.Focus();
            return;
        }

        var parts = Subnet.Split('.');
        var invalid = parts.Length < 3;
        if (!invalid) {
            for (var i = 0; i < parts.Length; i++) {
                var p = parts[i];
                if (!string.IsNullOrEmpty(p) && !int.TryParse(p, out _)) { invalid = true; break; }
            }
        }
        if (invalid) {
            MessageBox.Show("子網路格式錯誤，需為 x.x.x 格式（例如 192.168.1）", "驗證錯誤",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SubnetBox.Focus();
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }
}
