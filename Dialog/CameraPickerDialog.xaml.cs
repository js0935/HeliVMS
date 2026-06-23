using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace HeliVMS.Dialog;

public partial class CameraPickerDialog : Window {
    public new string Title { get; }

    public CameraPickerDialog(string title, ObservableCollection<CameraCheckItem> cameras) {
        InitializeComponent();
        Title = title;
        CameraCheckListBox.ItemsSource = cameras;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) {
        DialogResult = CameraCheckListBox.Items.Cast<CameraCheckItem>().Any(c => c.IsChecked);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) {
        DialogResult = false;
        Close();
    }
}

public class CameraCheckItem {
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsChecked { get; set; }
}
