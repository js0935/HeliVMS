using System.Globalization;
using System.Windows;
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
        Title = "HeliVMS 匯出簽章金鑰";
        Reload();
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
        SigningStatusText.Text = "已重新載入簽章金鑰。";
    }

    private void OnRotateClicked(object sender, RoutedEventArgs e)
    {
        // 兩段式：換發不可逆，第一次只佈署。
        if (!_rotateArm)
        {
            _rotateArm = true;
            RotateKeyButton.Content = "確認換發？";
            SigningStatusText.Text = "換發後現行金鑰會進入歷史，舊收據仍可用歷史指紋驗證。再按一次確認。";
            return;
        }

        _rotateArm = false;
        RotateKeyButton.Content = "換發金鑰";
        var actor = SessionContext.CurrentUser?.Username ?? "system";
        var rotation = _receipts.RotateSigningKey(actor);
        SigningStatusText.Text =
            $"已換發：{rotation.PreviousFingerprint} → {rotation.NewFingerprint}" +
            $"（{rotation.RotatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}）";
        Reload();
    }
}
