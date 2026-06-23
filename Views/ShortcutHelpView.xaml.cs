using System.Windows;
using System.Windows.Controls;

namespace HeliVMS.Views;

public partial class ShortcutHelpView : UserControl {
    public ShortcutHelpView() {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) {
        var window = Window.GetWindow(this);
        window?.Close();
    }
}
