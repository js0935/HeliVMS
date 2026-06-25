using System;
using System.IO;
using Serilog;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Dialog;
using HeliVMS.Models;
using HeliVMS.Services;
using Microsoft.Extensions.DependencyInjection;
using Clipboard = System.Windows.Clipboard;
using Microsoft.Win32;

namespace HeliVMS.Views;

public partial class LicenseView : UserControl {
    private readonly IEventService _eventService;
    private readonly ILicenseService _licenseService;

    public LicenseView() {
        InitializeComponent();
        _eventService = App.Services.GetRequiredService<IEventService>();
        _licenseService = App.Services.GetRequiredService<ILicenseService>();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) {
        LoadLicenseInfo();
        LoadSystemInfo();
    }

    private void LoadLicenseInfo() {
        try {
            var lic = _licenseService.CurrentLicense;

            LicenseNameText.Text = lic.ProductName;
            LicenseeText.Text = lic.Licensee;
            LicenseTypeText.Text = lic.LicenseType;
            ExpiryText.Text = lic.ExpiryDate ?? "--";
            MaxCamerasText.Text = lic.MaxCameras.ToString();

            if (!string.IsNullOrEmpty(lic.InvalidationReason)) {
                LicenseStatusText.Text = lic.InvalidationReason;
                LicenseStatusText.Foreground = FindResource("ErrorBrush") as System.Windows.Media.Brush
                    ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36));
            } else if (lic.IsValid) {
                LicenseStatusText.Text = lic.IsExpired ? "已過期" : "已授權";
                var statusBrush = lic.IsExpired
                    ? FindResource("WarningBrush") as System.Windows.Media.Brush
                    : FindResource("SuccessBrush") as System.Windows.Media.Brush;
                LicenseStatusText.Foreground = statusBrush;
            } else {
                LicenseStatusText.Text = "未授權";
                LicenseStatusText.Foreground = FindResource("ErrorBrush") as System.Windows.Media.Brush
                    ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x43, 0x36));
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] LoadLicense error: {Msg}", ex.Message);
        }
    }

    private void LoadSystemInfo() {
        try {
            VersionText.Text = "V1.0.0";
            OsVersionText.Text = Environment.OSVersion.VersionString;

            var machineId = LicenseService.ComputeMachineId();
            MachineIdText.Text = machineId;
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] LoadSysInfo error: {Msg}", ex.Message);
        }
    }

    private void ActivateLicenseButton_Click(object sender, RoutedEventArgs e) {
        try {
            var key = LicenseKeyBox.Text?.Trim();
            if (string.IsNullOrEmpty(key)) {
                MessageBox.Show("請輸入授權金鑰", "錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_licenseService.Activate(key)) {
                LoadLicenseInfo();
                MessageBox.Show("授權啟用成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            } else {
                MessageBox.Show("授權金鑰格式無效。\n格式：AAAA-BBBB-CCCC-DDDD", "錯誤",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] Activate license error: {Msg}", ex.Message);
            MessageBox.Show($"啟用失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveLicenseButton_Click(object sender, RoutedEventArgs e) {
        var password = InputPasswordDialog.Show("請輸入移除授權密碼", "移除授權確認");
        if (password is null) { return; }

        if (password != "hr22619219") {
            MessageBox.Show("密碼錯誤，無法移除授權。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _licenseService.Remove();
        LoadLicenseInfo();
        _eventService.LogWarning(EventCategories.Setting, "LicenseView", "授權已移除");
    }

    private void ExportLicenseButton_Click(object sender, RoutedEventArgs e) {
        try {
            if (!_licenseService.CurrentLicense.IsValid) {
                MessageBox.Show("目前無有效授權可匯出。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog {
                Title = "匯出授權檔案",
                Filter = "授權檔案 (*.lic)|*.lic|所有檔案 (*.*)|*.*",
                DefaultExt = ".lic",
                FileName = $"HeliVMS_license_{DateTime.Now:yyyyMMdd}.lic"
            };

            if (dialog.ShowDialog() == true) {
                var json = _licenseService.ExportLicense();
                File.WriteAllText(dialog.FileName, json, System.Text.Encoding.UTF8);
                _eventService.LogInfo(EventCategories.Setting, "LicenseView",
                    "授權已匯出", $"路徑: {dialog.FileName}");
                MessageBox.Show($"授權已匯出至：\n{dialog.FileName}", "匯出成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] Export license error: {Msg}", ex.Message);
            MessageBox.Show($"匯出失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportLicenseButton_Click(object sender, RoutedEventArgs e) {
        try {
            var dialog = new OpenFileDialog {
                Title = "匯入授權檔案",
                Filter = "授權檔案 (*.lic)|*.lic|所有檔案 (*.*)|*.*",
                DefaultExt = ".lic"
            };

            if (dialog.ShowDialog() == true) {
                if (_licenseService.ImportLicense(dialog.FileName)) {
                    LoadLicenseInfo();
                    _eventService.LogInfo(EventCategories.Setting, "LicenseView",
                        "授權已匯入", $"來源: {dialog.FileName}");
                    MessageBox.Show("授權匯入成功！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                } else {
                    MessageBox.Show("授權檔案格式無效。", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] Import license error: {Msg}", ex.Message);
            MessageBox.Show($"匯入失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyMachineId_Click(object sender, RoutedEventArgs e) {
        try {
            var machineId = MachineIdText.Text;
            if (!string.IsNullOrEmpty(machineId)) {
                Clipboard.SetText(machineId);
                MessageBox.Show("設備碼已複製到剪貼簿。", "複製成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] Copy machine ID error: {Msg}", ex.Message);
        }
    }
}
