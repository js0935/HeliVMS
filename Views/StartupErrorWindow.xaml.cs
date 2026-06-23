using System.Windows;
using System.Windows.Media;

namespace HeliVMS.Views;

public partial class StartupErrorWindow : Window {
    public StartupErrorWindow(string errorMessage) {
        InitializeComponent();
        ErrorDetailsBox.Text = errorMessage;
    }

    private void CopyError_Click(object sender, RoutedEventArgs e) {
        try {
            Clipboard.SetText(ErrorDetailsBox.Text);
        } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) {
        Application.Current.Shutdown();
    }
}