using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// POS 交易視窗（M93/M104/M113，§14.7 #8）：把 <see cref="POSEventRepository"/> 的去重匯入、
/// 區間查詢與 <see cref="PosReconciliation"/> 對帳拉到桌面。這些後端 API 先前完全沒有呼叫端。
/// </summary>
public partial class PosWindow : Window
{
    private static readonly TimeSpan KeyWindow = TimeSpan.FromMinutes(2);

    private readonly POSEventRepository _pos;
    private readonly AlarmEventRepository _events;

    public PosWindow(SqliteStore store)
    {
        _pos = new POSEventRepository(store);
        _events = new AlarmEventRepository(store);
        InitializeComponent();
        ApplyI18n();
        PosFromDate.SelectedDate = DateTime.Today;
        PosToDate.SelectedDate = DateTime.Today;
        Refresh();
    }

    /// <summary>依現況語言套用標題、欄位與按鈕文字（M57）。新開視窗以新語言顯示。</summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Pos.Title");
        HeadingText.Text = Localizer.T("Pos.Heading");
        DeviceIdLabel.Text = Localizer.T("Pos.DeviceId");
        RegisterLabel.Text = Localizer.T("Pos.Register");
        FromLabel.Text = Localizer.T("Pos.From");
        ToLabel.Text = Localizer.T("Pos.To");
        PosQueryButton.Content = Localizer.T("Pos.Query");
        PosImportButton.Content = Localizer.T("Pos.Import");
        PosImportButton.ToolTip = Localizer.T("Pos.ImportTip");
        PosReconcileButton.Content = Localizer.T("Pos.Reconcile");
        PosReconcileButton.ToolTip = Localizer.T("Pos.ReconcileTip");

        if (PosList.View is GridView grid && grid.Columns.Count >= 5)
        {
            grid.Columns[0].Header = Localizer.T("Pos.ColTime");
            grid.Columns[1].Header = Localizer.T("Pos.ColDevice");
            grid.Columns[2].Header = Localizer.T("Pos.ColRegister");
            grid.Columns[3].Header = Localizer.T("Pos.ColTransaction");
            grid.Columns[4].Header = Localizer.T("Pos.ColAmount");
        }
    }

    private bool TryReadRange(out int? deviceId, out DateTime fromUtc, out DateTime toUtc)
    {
        deviceId = null;
        fromUtc = default;
        toUtc = default;

        var deviceText = PosDeviceBox.Text.Trim();
        if (deviceText.Length > 0)
        {
            if (!int.TryParse(deviceText, out var d))
            {
                PosStatusText.Text = Localizer.T("Pos.DeviceIdInvalid");
                return false;
            }

            deviceId = d;
        }

        if (PosFromDate.SelectedDate is not { } from || PosToDate.SelectedDate is not { } to)
        {
            PosStatusText.Text = Localizer.T("Pos.PickDates");
            return false;
        }

        fromUtc = from.Date.ToUniversalTime();
        toUtc = to.Date.AddDays(1).ToUniversalTime();
        return true;
    }

    private void OnQueryClicked(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (!TryReadRange(out var deviceId, out var fromUtc, out var toUtc))
        {
            return;
        }

        var register = PosRegisterBox.Text.Trim();
        IReadOnlyList<POSEvent> items;
        if (register.Length > 0)
        {
            if (deviceId is null)
            {
                PosStatusText.Text = Localizer.T("Pos.RegisterNeedsDevice");
                return;
            }

            items = _pos.QueryByRegister(deviceId.Value, register, fromUtc, toUtc, 500);
        }
        else
        {
            items = _pos.Query(deviceId, fromUtc, toUtc, 500);
        }

        PosList.ItemsSource = items
            .Select(x => new
            {
                Time = TimeZoneInfo.ConvertTimeFromUtc(x.OccurredAtUtc, TimeZoneInfo.Local)
                    .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Device = x.DeviceId,
                Register = x.RegisterId,
                x.TransactionNo,
                Amount = (x.AmountCents / 100m).ToString("0.00", CultureInfo.InvariantCulture),
            })
            .ToList();
        PosStatusText.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("Pos.Found"), items.Count);
        PosReconText.Text = string.Empty;
    }

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localizer.T("Pos.ImportTitle"),
            Filter = Localizer.T("Pos.ImportFilter"),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!int.TryParse(PosDeviceBox.Text.Trim(), out var deviceId) || deviceId <= 0)
        {
            PosStatusText.Text = Localizer.T("Pos.ImportDeviceRequired");
            return;
        }

        int inserted = 0;
        int duplicate = 0;
        int skipped = 0;

        try
        {
            foreach (var raw in File.ReadAllLines(dialog.FileName))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("register", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fields = line.Split(',');
                if (fields.Length < 4 ||
                    !decimal.TryParse(fields[2].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ||
                    !DateTime.TryParse(fields[3].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var occurred))
                {
                    skipped++;
                    continue;
                }

                var amountCents = (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
                var (_, isNew) = _pos.InsertDedupe(
                    deviceId,
                    fields[0].Trim(),
                    fields[1].Trim(),
                    amountCents,
                    occurred.ToUniversalTime(),
                    KeyWindow);
                if (isNew)
                {
                    inserted++;
                }
                else
                {
                    duplicate++;
                }
            }
        }
        catch (Exception ex)
        {
            PosStatusText.Text = string.Format(
                CultureInfo.InvariantCulture, Localizer.T("Pos.ImportFailed"), ex.Message);
            return;
        }

        PosStatusText.Text = string.Format(
            CultureInfo.InvariantCulture, Localizer.T("Pos.ImportDone"), inserted, duplicate, skipped);
        Refresh();
    }

    private void OnReconcileClicked(object sender, RoutedEventArgs e)
    {
        if (!TryReadRange(out var deviceId, out var fromUtc, out var toUtc))
        {
            return;
        }

        var register = PosRegisterBox.Text.Trim();
        IReadOnlyList<POSEvent> txns;
        if (register.Length > 0)
        {
            if (deviceId is null)
            {
                PosStatusText.Text = Localizer.T("Pos.ReconcileNeedsDevice");
                return;
            }

            txns = _pos.QueryByRegister(deviceId.Value, register, fromUtc, toUtc);
        }
        else
        {
            txns = _pos.Query(deviceId, fromUtc, toUtc);
        }

        var candidates = _events.ListByRange(null, fromUtc, toUtc);
        var summary = PosReconciliation.Compute(txns, candidates, static ev => ev.StartUtc, KeyWindow);
        PosReconText.Text = string.Format(
            CultureInfo.InvariantCulture,
            Localizer.T("Pos.Recon"),
            summary.Total,
            summary.Matched,
            summary.Unmatched,
            summary.Duplicates,
            candidates.Count);
    }
}
