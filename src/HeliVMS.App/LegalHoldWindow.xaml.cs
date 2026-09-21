using System.Globalization;
using System.Windows;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>保存鎖定視窗（M66，§14.1 #13）：指定時段豁免配額汰除；沖銷留稽核（admin 限定）。</summary>
public partial class LegalHoldWindow : Window
{
    private readonly LegalHoldRepository _holds;

    public LegalHoldWindow(SqliteStore store, bool allowRevoke)
    {
        _holds = new LegalHoldRepository(store);
        InitializeComponent();

        var channels = new ChannelRepository(store).List();
        LegalHoldChannelCombo.ItemsSource = channels.Select(c => new ChannelItem(c.Id, $"頻道 {c.Id}（{c.Name}）")).ToList();
        LegalHoldChannelCombo.DisplayMemberPath = "Label";
        LegalHoldChannelCombo.SelectedIndex = channels.Count > 0 ? 0 : -1;

        var now = DateTime.Now;
        LegalHoldFromBox.Text = now.AddHours(-24).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        LegalHoldToBox.Text = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        LegalHoldRevokeButton.IsEnabled = allowRevoke;
        if (!allowRevoke)
        {
            LegalHoldStatusText.Text = "目前登入非管理員：僅可檢視，沖銷需管理員權限。";
        }

        RefreshList();
    }

    private sealed record ChannelItem(int Id, string Label);

    private sealed record HoldRow(long Id, int ChannelId, string FromText, string ToText, string Reason, string CreatedBy, string Status);

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LegalHoldChannelCombo.SelectedItem is not ChannelItem channel)
            {
                LegalHoldStatusText.Text = "尚未選擇頻道。";
                return;
            }

            if (!TryParseTime(LegalHoldFromBox.Text, out var fromLocal) ||
                !TryParseTime(LegalHoldToBox.Text, out var toLocal))
            {
                LegalHoldStatusText.Text = "時間格式應為 yyyy-MM-dd HH:mm。";
                return;
            }

            var fromUtc = fromLocal.ToUniversalTime();
            var toUtc = toLocal.ToUniversalTime();
            if (toUtc <= fromUtc)
            {
                LegalHoldStatusText.Text = "訖時間須晚於起時間。";
                return;
            }

            var reason = LegalHoldReasonBox.Text.Trim();
            if (reason.Length == 0)
            {
                LegalHoldStatusText.Text = "原因不可為空。";
                return;
            }

            var id = _holds.Add(channel.Id, fromUtc, toUtc, reason, SessionContext.CurrentUser?.Username ?? "?", DateTime.UtcNow);
            RefreshList();
            LegalHoldStatusText.Text = $"已加鎖 id={id}（頻道 {channel.Id}）。";
        }
        catch (Exception ex)
        {
            LegalHoldStatusText.Text = $"加鎖失敗：{ex.Message}";
        }
    }

    private void OnRevokeClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LegalHoldList.SelectedItem is not HoldRow row)
            {
                LegalHoldStatusText.Text = "請先在清單選取要沖銷的鎖定。";
                return;
            }

            if (!_holds.Revoke(row.Id, SessionContext.CurrentUser?.Username ?? "?", "使用者在 UI 沖銷", DateTime.UtcNow))
            {
                LegalHoldStatusText.Text = $"沖銷失敗：鎖定 {row.Id} 不存在或已沖銷。";
                return;
            }

            RefreshList();
            LegalHoldStatusText.Text = $"已沖銷鎖定 id={row.Id}。";
        }
        catch (Exception ex)
        {
            LegalHoldStatusText.Text = $"沖銷失敗：{ex.Message}";
        }
    }

    private void RefreshList()
    {
        LegalHoldList.ItemsSource = _holds.ListAll()
            .OrderBy(h => h.IsActive ? 0 : 1)
            .ThenBy(h => h.FromUtc)
            .Select(h => new HoldRow(
                h.Id,
                h.ChannelId,
                SqliteStore.Iso(h.FromUtc),
                SqliteStore.Iso(h.ToUtc),
                h.Reason,
                h.CreatedBy,
                h.IsActive ? "作用中" : "已沖銷"))
            .ToList();
    }

    private static bool TryParseTime(string text, out DateTime local)
        => DateTime.TryParseExact(
            text.Trim(),
            "yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out local);
}