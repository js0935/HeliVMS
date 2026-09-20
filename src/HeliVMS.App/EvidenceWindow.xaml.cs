using System.IO;
using System.Windows;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 數位證據完整性視窗（M53，§14.7 #5；§14.3 證物線）：對證據目錄建立含 SHA-256
/// 清單與數位簽章的 manifest，並可重新驗證完整性（OK／TAMPERED／MISSING／EXTRA）。
/// </summary>
public partial class EvidenceWindow : Window
{
    private readonly SqliteStore _store;
    private readonly EvidenceManifestService _service;

    public EvidenceWindow(SqliteStore store, string? directory = null)
    {
        _store = store;
        _service = new EvidenceManifestService(store);

        InitializeComponent();

        EvidenceDirBox.Text = directory ?? Path.Combine(
            Environment.GetEnvironmentVariable("HELIVMS_DATA") ?? @"C:\HeliVMSData",
            "evidence");
    }

    private sealed record EvidenceRow(string RelPath, string Sha256, string SizeLabel, string StatusLabel);

    private string? EnsureDirectory()
    {
        var dir = EvidenceDirBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            EvidenceStatusText.Text = "請先輸入證據目錄。";
            return null;
        }

        if (!Directory.Exists(dir))
        {
            EvidenceStatusText.Text = $"目錄不存在：{dir}";
            return null;
        }

        return Path.GetFullPath(dir);
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇證據目錄",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            EvidenceDirBox.Text = dialog.FolderName;
        }
    }

    private void OnCreateClicked(object sender, RoutedEventArgs e)
    {
        var dir = EnsureDirectory();
        if (dir is null)
        {
            return;
        }

        try
        {
            var record = _service.Save(dir);
            var verify = _service.Verify(dir);
            FillList(verify.Items.OrderBy(i => i.RelPath));
            var info = _service.VerifySigned(record.ManifestJson);
            EvidenceStatusText.Text =
                $"已建立 manifest（{verify.OkCount} 檔，已簽署數位簽章）。" +
                (info.Valid ? "" : "（簽章驗證失敗）");
        }
        catch (Exception ex)
        {
            EvidenceStatusText.Text = $"建立失敗：{ex.Message}";
        }
    }

    private void OnVerifyClicked(object sender, RoutedEventArgs e)
    {
        var dir = EnsureDirectory();
        if (dir is null)
        {
            return;
        }

        try
        {
            var result = _service.Verify(dir);
            FillList(result.Items.OrderBy(i => i.RelPath));
            EvidenceStatusText.Text =
                $"完整性：{result.OkCount} 檔 OK / {result.TamperedCount} 篡改 / " +
                $"{result.MissingCount} 缺漏 / {result.ExtraCount} 額外。" +
                (result.OverallOk ? " 驗證通過。" : " 驗證未通過！");
        }
        catch (Exception ex)
        {
            EvidenceStatusText.Text = $"驗證失敗：{ex.Message}";
        }
    }

    private void FillList(IEnumerable<EvidenceItem> items)
    {
        EvidenceList.ItemsSource = items.Select(i => new EvidenceRow(
            i.RelPath,
            i.Sha256,
            i.Size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
            i.Status switch
            {
                EvidenceItemStatus.Ok => "OK",
                EvidenceItemStatus.Tampered => "TAMPERED",
                EvidenceItemStatus.Missing => "MISSING",
                EvidenceItemStatus.Extra => "EXTRA",
                _ => i.Status.ToString(),
            })).ToList();
    }
}