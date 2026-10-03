using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 匯出簽章金鑰（M241）：顯示現行指紋供外部比對，並可換發金鑰。
/// 換發後舊金鑰留在歷史，<see cref="ExportReceiptService.Verify(string, TrustedSignerSet?)"/>
/// 會同時信任現行與歷史指紋，先前簽出的收據不會因換發而全部變成無效。
/// </summary>
public partial class SigningKeysWindow : Window
{
    private readonly ExportReceiptService _receipts;
    private bool _rotateArm;

    public SigningKeysWindow(SqliteStore store)
    {
        _receipts = new ExportReceiptService(store);
        InitializeComponent();
        ApplyI18n();
        Reload();
    }

    /// <summary>依現況語言套用標題、欄位與按鈕文字（M57）。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Signing.Title");
        CurrentHeadingText.Text = Localizer.T("Signing.CurrentHeading");
        HistoryHeadingText.Text = Localizer.T("Signing.HistoryHeading");
        RefreshKeysButton.Content = Localizer.T("Signing.Refresh");
        RefreshKeysButton.ToolTip = Localizer.T("Signing.RefreshTip");
        RotateKeyButton.Content = Localizer.T("Signing.Rotate");
        RotateKeyButton.ToolTip = Localizer.T("Signing.RotateTip");

        if (KeyHistoryList.View is GridView grid && grid.Columns.Count >= 2)
        {
            grid.Columns[0].Header = Localizer.T("Signing.ColFingerprint");
            grid.Columns[1].Header = Localizer.T("Signing.ColRetired");
        }
    }

    private sealed record KeyRow(string Fingerprint, string RetiredLabel);

    private void Reload()
    {
        CurrentFingerprintBox.Text = _receipts.SignerFingerprint();
        KeyHistoryList.ItemsSource = _receipts.SigningKeyHistory()
            .Select(k => new KeyRow(
                k.Fingerprint,
                k.RetiredUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))
            .ToList();
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        Reload();
        SigningStatusText.Text = Localizer.T("Signing.Refreshed");
    }

    private void OnRotateClicked(object sender, RoutedEventArgs e)
    {
        // 兩段式：換發不可逆，第一次只佈署。
        if (!_rotateArm)
        {
            _rotateArm = true;
            RotateKeyButton.Content = Localizer.T("Signing.RotateConfirm");
            SigningStatusText.Text = Localizer.T("Signing.ConfirmHint");
            return;
        }

        _rotateArm = false;
        RotateKeyButton.Content = Localizer.T("Signing.Rotate");
        var actor = SessionContext.CurrentUser?.Username ?? "system";
        var rotation = _receipts.RotateSigningKey(actor);
        SigningStatusText.Text = string.Format(
            CultureInfo.InvariantCulture,
            Localizer.T("Signing.Rotated"),
            rotation.PreviousFingerprint,
            rotation.NewFingerprint,
            rotation.RotatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Reload();
    }
}
