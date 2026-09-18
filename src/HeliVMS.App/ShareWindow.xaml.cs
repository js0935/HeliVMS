using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.App.Services;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 外部安全共享視窗（M51，§14.7 #4）：建立具到期／次數／密碼的分享連結，並管理（複製／撤銷／刪除／清理）。
/// </summary>
public partial class ShareWindow : Window
{
    private readonly SqliteStore _store;
    private readonly string _dataRoot;
    private readonly SettingsRepository _settings;
    private readonly ShareLinkService _links;

    public ShareWindow(SqliteStore store, string dataRoot, string? initialPath = null)
    {
        _store = store;
        _dataRoot = dataRoot;
        _settings = new SettingsRepository(store);
        _links = new ShareLinkService(store);

        InitializeComponent();

        ShareKindCombo.ItemsSource = new[] { ShareKind.Segment, ShareKind.Snapshot, ShareKind.Evidence };
        ShareKindCombo.SelectedIndex = 0;
        ShareExpireBox.Text = "24";
        ShareMaxUsesBox.Text = "0";
        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            SharePathBox.Text = initialPath;
        }

        Reload();
    }

    private sealed record ShareRow(long Id, string Token, string Title, string Detail, string CreatedBy);

    private string BuildLink(string token)
    {
        var port = int.TryParse(_settings.Get(ShareHost.PortKey), out var p) && p > 0 ? p : ShareHost.DefaultPort;
        return $"{ShareHost.ResolveBaseUrl(_settings, port)}/share/{token}";
    }

    private void Reload()
    {
        var now = DateTime.UtcNow;
        var rows = new List<ShareRow>();
        foreach (var record in _links.List())
        {
            var status = record.Revoked
                ? "已撤銷"
                : record.ExpiresAt is { Length: > 0 } exp && SqliteStore.FromIso(exp) <= now
                    ? "已過期"
                    : record.MaxUses > 0 && record.UseCount >= record.MaxUses
                        ? "次數用盡"
                        : "有效";

            var expiry = record.ExpiresAt is { Length: > 0 } e
                ? $"{SqliteStore.FromIso(e):MM-dd HH:mm}"
                : "不限";
            var uses = record.MaxUses > 0 ? $"{record.UseCount}/{record.MaxUses}" : $"{record.UseCount}/∞";
            var lockLabel = record.HasPassword ? "加密" : "公開";

            rows.Add(new ShareRow(
                record.Id,
                record.Token,
                $"#{record.Id} {record.Kind} {record.Label ?? "(無標籤)"} {record.Token[..Math.Min(10, record.Token.Length)]}…",
                $"{status}｜到期 {expiry}｜用 {uses}｜{lockLabel}",
                record.CreatedBy ?? "—"));
        }

        ShareList.ItemsSource = rows;
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選擇要分享的檔案",
            InitialDirectory = Directory.Exists(_dataRoot) ? _dataRoot : null,
        };

        if (dlg.ShowDialog() == true)
        {
            SharePathBox.Text = dlg.FileName;
        }
    }

    private void OnCreateClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var kind = ShareKindCombo.SelectedItem as string ?? ShareKind.Segment;
            var label = string.IsNullOrWhiteSpace(ShareLabelBox.Text) ? null : ShareLabelBox.Text.Trim();

            DateTime? expires = null;
            if (double.TryParse(ShareExpireBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && hours > 0)
            {
                expires = DateTime.UtcNow.AddHours(hours);
            }

            var maxUses = int.TryParse(ShareMaxUsesBox.Text, out var uses) && uses > 0 ? uses : 0;
            var password = string.IsNullOrEmpty(SharePasswordBox.Password) ? null : SharePasswordBox.Password;

            var record = _links.Create(
                kind,
                SharePathBox.Text.Trim(),
                new[] { _dataRoot },
                DateTime.UtcNow,
                label,
                SessionContext.CurrentUser?.Username,
                expires,
                maxUses,
                password);

            var link = BuildLink(record.Token);
            ShareStatusText.Text = $"已建立分享連結：{link}";
            try
            {
                Clipboard.SetText(link);
                ShareStatusText.Text += "（已複製到剪貼簿）";
            }
            catch (Exception)
            {
                // 剪貼簿暫時不可用時仍顯示連結
            }

            Reload();
        }
        catch (Exception ex)
        {
            ShareStatusText.Text = $"建立失敗：{ex.Message}";
        }
    }

    private ShareRow? Selected => ShareList.SelectedItem as ShareRow;

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Reload();

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            ShareStatusText.Text = "請先選擇一個分享連結。";
            return;
        }

        var link = BuildLink(row.Token);
        try
        {
            Clipboard.SetText(link);
            ShareStatusText.Text = $"已複製：{link}";
        }
        catch (Exception ex)
        {
            ShareStatusText.Text = $"複製失敗（{ex.Message}）：{link}";
        }
    }

    private void OnRevokeClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            ShareStatusText.Text = "請先選擇一個分享連結。";
            return;
        }

        _links.Revoke((int)row.Id);
        ShareStatusText.Text = $"已撤銷分享連結 #{row.Id}。";
        Reload();
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            ShareStatusText.Text = "請先選擇一個分享連結。";
            return;
        }

        _links.Delete((int)row.Id);
        ShareStatusText.Text = $"已刪除分享連結 #{row.Id}。";
        Reload();
    }

    private void OnPurgeClicked(object sender, RoutedEventArgs e)
    {
        var removed = _links.PurgeExpired(DateTime.UtcNow);
        ShareStatusText.Text = $"已清除 {removed} 筆過期分享連結。";
        Reload();
    }
}
