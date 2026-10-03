using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        ApplyI18n();

        EvidenceDirBox.Text = directory ?? Path.Combine(
            Environment.GetEnvironmentVariable("HELIVMS_DATA") ?? @"C:\HeliVMSData",
            "evidence");
    }

    /// <summary>依現況語言套用標題、標籤、按鈕與欄位文字。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Evidence.Title");
        EvidenceHeadingText.Text = Localizer.T("Evidence.Heading");
        EvidenceDirectoryLabel.Text = Localizer.T("Evidence.Directory");
        EvidenceBrowseButton.Content = Localizer.T("Evidence.Browse");
        EvidenceBrowseButton.ToolTip = Localizer.T("Evidence.BrowseTip");
        EvidenceCreateButton.Content = Localizer.T("Evidence.Create");
        EvidenceCreateButton.ToolTip = Localizer.T("Evidence.CreateTip");
        EvidenceVerifyButton.Content = Localizer.T("Evidence.Verify");
        EvidenceVerifyButton.ToolTip = Localizer.T("Evidence.VerifyTip");

        if (EvidenceList.View is GridView grid && grid.Columns.Count >= 4)
        {
            grid.Columns[0].Header = Localizer.T("Evidence.ColFile");
            grid.Columns[2].Header = Localizer.T("Evidence.ColSize");
            grid.Columns[3].Header = Localizer.T("Evidence.ColStatus");
        }
    }

    private sealed record EvidenceRow(string RelPath, string Sha256, string SizeLabel, string StatusLabel);

    private string? EnsureDirectory()
    {
        var dir = EvidenceDirBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            EvidenceStatusText.Text = Localizer.T("Evidence.NoDirectory");
            return null;
        }

        if (!Directory.Exists(dir))
        {
            EvidenceStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Evidence.DirectoryMissing"), dir);
            return null;
        }

        return Path.GetFullPath(dir);
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Localizer.T("Evidence.PickDirectory"),
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
            EvidenceStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Evidence.Created"), verify.OkCount) +
                (info.Valid ? "" : Localizer.T("Evidence.SignatureFailed"));
        }
        catch (Exception ex)
        {
            EvidenceStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Evidence.CreateFailed"), ex.Message);
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
            EvidenceStatusText.Text = string.Format(
                CultureInfo.InvariantCulture,
                Localizer.T("Evidence.Summary"),
                result.OkCount, result.TamperedCount, result.MissingCount, result.ExtraCount,
                result.OverallOk ? Localizer.T("Evidence.Pass") : Localizer.T("Evidence.Fail"));
        }
        catch (Exception ex)
        {
            EvidenceStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Evidence.VerifyFailed"), ex.Message);
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