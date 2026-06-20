using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Models;
using Microsoft.Win32;

namespace HeliVMS.Controls;

public partial class StorageManagerPage : UserControl
{
    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "storage_locations.json");

    private readonly ObservableCollection<StorageLocation> _locations = new();

    public StorageManagerPage()
    {
        InitializeComponent();
        LoadLocations();
        StorageList.ItemsSource = _locations;
    }

    private void LoadLocations()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var list = JsonSerializer.Deserialize<List<StorageLocation>>(json);
                if (list?.Count > 0)
                {
                    foreach (var loc in list)
                    {
                        _locations.Add(loc);
                    }
                    return;
                }
            }
        }
        catch
        {
        }

        if (_locations.Count == 0)
        {
            _locations.Add(new StorageLocation
            {
                Path = @"D:\Recordings",
                IsActive = true,
                Description = "預設錄影路徑"
            });
        }
    }

    private void SaveLocations()
    {
        try
        {
            var json = JsonSerializer.Serialize(_locations.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"儲存錄影位置時發生錯誤：{ex.Message}", "錯誤",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsSystemDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.Equals(root, @"C:\", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string? PickFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "選擇錄影儲存資料夾"
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void AddPath_Click(object sender, RoutedEventArgs e)
    {
        var folder = PickFolder();
        if (folder is null) { return; }

        if (IsSystemDrive(folder))
        {
            MessageBox.Show("系統磁碟機 C 不適合用於錄影儲存，請選擇其他磁碟機（例如 D:）",
                "系統警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool exists = false;
        for (int i = 0; i < _locations.Count; i++)
        {
            if (string.Equals(_locations[i].Path, folder, StringComparison.OrdinalIgnoreCase))
            { exists = true; break; }
        }
        if (exists)
        {
            MessageBox.Show("該路徑已經存在於列表中", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var location = new StorageLocation
        {
            Path = folder,
            IsActive = true,
            Description = $"錄影位置 {_locations.Count + 1}"
        };
        _locations.Add(location);
    }

    private void EditPath_Click(object sender, RoutedEventArgs e)
    {
        if (StorageList.SelectedItem is not StorageLocation selected) { return; }

        var folder = PickFolder();
        if (folder is null) { return; }

        if (IsSystemDrive(folder))
        {
            MessageBox.Show("系統磁碟機 C 不適合用於錄影儲存，請選擇其他磁碟機（例如 D:）",
                "系統警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool exists = false;
        for (int i = 0; i < _locations.Count; i++)
        {
            var l = _locations[i];
            if (l != selected && string.Equals(l.Path, folder, StringComparison.OrdinalIgnoreCase))
            { exists = true; break; }
        }
        if (exists)
        {
            MessageBox.Show("該路徑已經存在於列表中", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        selected.Path = folder;
        StorageList.Items.Refresh();
    }

    private void RemovePath_Click(object sender, RoutedEventArgs e)
    {
        if (StorageList.SelectedItem is not StorageLocation selected) { return; }

        if (_locations.Count <= 1)
        {
            MessageBox.Show("無法移除最後一個儲存位置", "移除位置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show($"確定要移除 {selected.Path} 嗎？",
            "確認移除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            _locations.Remove(selected);
        }
    }

    private void StorageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = StorageList.SelectedItem is not null;
        EditPathButton.IsEnabled = hasSelection;
        RemovePathButton.IsEnabled = hasSelection;
    }

    private void StorageItem_Changed(object sender, RoutedEventArgs e)
    {
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var cDrivePaths = new List<StorageLocation>();
        for (int i = 0; i < _locations.Count; i++)
        {
            var l = _locations[i];
            if (IsSystemDrive(l.Path))
            {
                cDrivePaths.Add(l);
            }
        }
        if (cDrivePaths.Count > 0)
        {
            MessageBox.Show($"以下路徑位於系統碟 C：，不建議作為錄影儲存位置：\n{string.Join("\n", cDrivePaths.Select(l => l.Path))}",
                "系統警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveLocations();
        MessageBox.Show("儲存路徑設定已成功儲存", "完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
