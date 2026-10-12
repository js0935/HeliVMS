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
        ApplyI18n();

        // remote 旗標限定（M220）：分享是對外的唯一外洩面，未授權時連建立鈕都不留。
        new LicenseUiGate(store).Apply(ShareCreateButton, LicenseFeatures.Remote);

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

    /// <summary>
    /// 套用目前語系到共享視窗的固定文字（M57 §14.7）。XAML 已無硬編中文，文字集中於此，
    /// 由 WpfI18nContractTests 的棘輪釘住三語齊備。
    /// </summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Share.Title");
        DescriptionText.Text = Localizer.T("Share.Description");
        PathLabelText.Text = Localizer.T("Share.Path");
        ShareBrowseButton.Content = Localizer.T("Share.Browse");
        ShareBrowseButton.ToolTip = Localizer.T("Share.BrowseTip");
        KindLabelText.Text = Localizer.T("Share.Kind");
        LabelLabelText.Text = Localizer.T("Share.Label");
        ExpireLabelText.Text = Localizer.T("Share.Expire");
        MaxUsesLabelText.Text = Localizer.T("Share.MaxUses");
        PasswordLabelText.Text = Localizer.T("Share.Password");
        ShareCreateButton.Content = Localizer.T("Share.Create");
        ShareCreateButton.ToolTip = Localizer.T("Share.CreateTip");
        ShareColTitle.Header = Localizer.T("Share.ColTitle");
        ShareColStatus.Header = Localizer.T("Share.ColStatus");
        ShareColUser.Header = Localizer.T("Share.ColUser");
        ShareRefreshButton.Content = Localizer.T("Share.Refresh");
        ShareRefreshButton.ToolTip = Localizer.T("Share.RefreshTip");
        ShareCopyButton.Content = Localizer.T("Share.CopyLink");
        ShareCopyButton.ToolTip = Localizer.T("Share.CopyLinkTip");
        ShareRevokeButton.Content = Localizer.T("Share.Revoke");
        ShareRevokeButton.ToolTip = Localizer.T("Share.RevokeTip");
        ShareDeleteButton.Content = Localizer.T("Share.Delete");
        ShareDeleteButton.ToolTip = Localizer.T("Share.DeleteTip");
        SharePurgeButton.Content = Localizer.T("Share.Purge");
        SharePurgeButton.ToolTip = Localizer.T("Share.PurgeTip");
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
                ? Localizer.T("Share.StatusRevoked")
                : record.ExpiresAt is { Length: > 0 } exp && SqliteStore.FromIso(exp) <= now
                    ? Localizer.T("Share.StatusExpired")
                    : record.MaxUses > 0 && record.UseCount >= record.MaxUses
                        ? Localizer.T("Share.StatusExhausted")
                        : Localizer.T("Share.StatusActive");

            var expiry = record.ExpiresAt is { Length: > 0 } e
                ? $"{SqliteStore.FromIso(e):MM-dd HH:mm}"
                : Localizer.T("Share.ExpiryNone");
            var uses = record.MaxUses > 0 ? $"{record.UseCount}/{record.MaxUses}" : $"{record.UseCount}/∞";
            var lockLabel = record.HasPassword ? Localizer.T("Share.LockEncrypted") : Localizer.T("Share.LockPublic");

            var title = string.Format(
                Localizer.T("Share.RowTitle"),
                record.Id,
                record.Kind,
                record.Label ?? Localizer.T("Share.NoLabel"),
                record.Token[..Math.Min(10, record.Token.Length)]);

            rows.Add(new ShareRow(
                record.Id,
                record.Token,
                title,
                string.Format(Localizer.T("Share.RowDetail"), status, expiry, uses, lockLabel),
                record.CreatedBy ?? "—"));
        }

        ShareList.ItemsSource = rows;
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.T("Share.PickTitle"),
            InitialDirectory = Directory.Exists(_dataRoot) ? _dataRoot : null,
        };

        if (dlg.ShowDialog() == true)
        {
            SharePathBox.Text = dlg.FileName;
        }
    }

    private void OnCreateClicked(object sender, RoutedEventArgs e)
    {
        // remote 旗標限定（M220）：鈕藏了還得擋 handler。
        var gate = new LicenseUiGate(_store);
        if (!gate.Allows(LicenseFeatures.Remote))
        {
            ShareStatusText.Text = gate.DenialMessage(LicenseFeatures.Remote);
            return;
        }

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
            ShareStatusText.Text = string.Format(Localizer.T("Share.Created"), link);
            try
            {
                Clipboard.SetText(link);
                ShareStatusText.Text += Localizer.T("Share.CopiedSuffix");
            }
            catch (Exception)
            {
                // 剪貼簿暫時不可用時仍顯示連結
            }

            Reload();
        }
        catch (Exception ex)
        {
            ShareStatusText.Text = string.Format(Localizer.T("Share.CreateFailed"), ex.Message);
        }
    }

    private ShareRow? Selected => ShareList.SelectedItem as ShareRow;

    private void OnRefreshClicked(object sender, RoutedEventArgs e) => Reload();

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            ShareStatusText.Text = Localizer.T("Share.NeedSelect");
            return;
        }

        var link = BuildLink(row.Token);
        try
        {
            Clipboard.SetText(link);
            ShareStatusText.Text = string.Format(Localizer.T("Share.Copied"), link);
        }
        catch (Exception ex)
        {
            ShareStatusText.Text = string.Format(Localizer.T("Share.CopyFailed"), ex.Message, link);
        }
    }

    private void OnRevokeClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            ShareStatusText.Text = Localizer.T("Share.NeedSelect");
            return;
        }

        _links.Revoke((int)row.Id);
        ShareStatusText.Text = string.Format(Localizer.T("Share.Revoked"), row.Id);
        Reload();
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            ShareStatusText.Text = Localizer.T("Share.NeedSelect");
            return;
        }

        _links.Delete((int)row.Id);
        ShareStatusText.Text = string.Format(Localizer.T("Share.Deleted"), row.Id);
        Reload();
    }

    private void OnPurgeClicked(object sender, RoutedEventArgs e)
    {
        var removed = _links.PurgeExpired(DateTime.UtcNow);
        ShareStatusText.Text = string.Format(Localizer.T("Share.Purged"), removed);
        Reload();
    }
}
