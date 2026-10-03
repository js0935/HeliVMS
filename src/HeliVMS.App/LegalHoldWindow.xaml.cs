using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
        ApplyI18n();

        var channels = new ChannelRepository(store).List();
        LegalHoldChannelCombo.ItemsSource = channels
            .Select(c => new ChannelItem(c.Id, string.Format(CultureInfo.InvariantCulture, Localizer.T("LegalHold.ChannelItem"), c.Id, c.Name)))
            .ToList();
        LegalHoldChannelCombo.DisplayMemberPath = "Label";
        LegalHoldChannelCombo.SelectedIndex = channels.Count > 0 ? 0 : -1;

        var now = DateTime.Now;
        LegalHoldFromBox.Text = now.AddHours(-24).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        LegalHoldToBox.Text = now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        LegalHoldRevokeButton.IsEnabled = allowRevoke;
        if (!allowRevoke)
        {
            LegalHoldStatusText.Text = Localizer.T("LegalHold.NonAdmin");
        }

        RefreshList();
    }

    /// <summary>依現況語言套用標題、篩選標籤、按鈕與欄位文字。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("LegalHold.Title");
        LegalHoldHeadingText.Text = Localizer.T("LegalHold.Heading");
        LegalHoldChannelLabel.Text = Localizer.T("LegalHold.Channel");
        LegalHoldFromLabel.Text = Localizer.T("LegalHold.From");
        LegalHoldToLabel.Text = Localizer.T("LegalHold.To");
        LegalHoldReasonLabel.Text = Localizer.T("LegalHold.Reason");
        LegalHoldTimeFormatText.Text = Localizer.T("LegalHold.TimeFormat");
        LegalHoldAddButton.Content = Localizer.T("LegalHold.Add");
        LegalHoldAddButton.ToolTip = Localizer.T("LegalHold.AddTip");
        LegalHoldRevokeButton.Content = Localizer.T("LegalHold.Revoke");
        LegalHoldRevokeButton.ToolTip = Localizer.T("LegalHold.RevokeTip");

        if (LegalHoldList.View is GridView grid && grid.Columns.Count >= 7)
        {
            grid.Columns[1].Header = Localizer.T("LegalHold.ColChannel");
            grid.Columns[2].Header = Localizer.T("LegalHold.ColFrom");
            grid.Columns[3].Header = Localizer.T("LegalHold.ColTo");
            grid.Columns[4].Header = Localizer.T("LegalHold.ColReason");
            grid.Columns[5].Header = Localizer.T("LegalHold.ColCreatedBy");
            grid.Columns[6].Header = Localizer.T("LegalHold.ColStatus");
        }
    }

    private sealed record ChannelItem(int Id, string Label);

    private sealed record HoldRow(long Id, int ChannelId, string FromText, string ToText, string Reason, string CreatedBy, string Status);

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LegalHoldChannelCombo.SelectedItem is not ChannelItem channel)
            {
                LegalHoldStatusText.Text = Localizer.T("LegalHold.NoChannel");
                return;
            }

            if (!TryParseTime(LegalHoldFromBox.Text, out var fromLocal) ||
                !TryParseTime(LegalHoldToBox.Text, out var toLocal))
            {
                LegalHoldStatusText.Text = Localizer.T("LegalHold.TimeFormatError");
                return;
            }

            var fromUtc = fromLocal.ToUniversalTime();
            var toUtc = toLocal.ToUniversalTime();
            if (toUtc <= fromUtc)
            {
                LegalHoldStatusText.Text = Localizer.T("LegalHold.RangeError");
                return;
            }

            var reason = LegalHoldReasonBox.Text.Trim();
            if (reason.Length == 0)
            {
                LegalHoldStatusText.Text = Localizer.T("LegalHold.ReasonRequired");
                return;
            }

            var id = _holds.Add(channel.Id, fromUtc, toUtc, reason, SessionContext.CurrentUser?.Username ?? "?", DateTime.UtcNow);
            RefreshList();
            LegalHoldStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("LegalHold.Added"), id, channel.Id);
        }
        catch (Exception ex)
        {
            LegalHoldStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("LegalHold.AddFailed"), ex.Message);
        }
    }

    private void OnRevokeClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LegalHoldList.SelectedItem is not HoldRow row)
            {
                LegalHoldStatusText.Text = Localizer.T("LegalHold.SelectToRevoke");
                return;
            }

            if (!_holds.Revoke(row.Id, SessionContext.CurrentUser?.Username ?? "?", Localizer.T("LegalHold.RevokeReason"), DateTime.UtcNow))
            {
                LegalHoldStatusText.Text = string.Format(
                    CultureInfo.InvariantCulture, Localizer.T("LegalHold.RevokeGone"), row.Id);
                return;
            }

            RefreshList();
            LegalHoldStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("LegalHold.Revoked"), row.Id);
        }
        catch (Exception ex)
        {
            LegalHoldStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("LegalHold.RevokeFailed"), ex.Message);
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
                h.IsActive ? Localizer.T("LegalHold.Active") : Localizer.T("LegalHold.RevokedState")))
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