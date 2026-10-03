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
        PosFromDate.SelectedDate = DateTime.Today;
        PosToDate.SelectedDate = DateTime.Today;
        Refresh();
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
                PosStatusText.Text = "設備 ID 必須是整數（留空表示不限）。";
                return false;
            }

            deviceId = d;
        }

        if (PosFromDate.SelectedDate is not { } from || PosToDate.SelectedDate is not { } to)
        {
            PosStatusText.Text = "請選擇起迄日期。";
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
                PosStatusText.Text = "依收銀機查詢時必須指定設備 ID。";
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
        PosStatusText.Text = $"查到 {items.Count} 筆交易";
        PosReconText.Text = string.Empty;
    }

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "匯入 POS 交易 CSV",
            Filter = "CSV (*.csv)|*.csv|所有檔案 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!int.TryParse(PosDeviceBox.Text.Trim(), out var deviceId) || deviceId <= 0)
        {
            PosStatusText.Text = "匯入前請輸入正的設備 ID。";
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
            PosStatusText.Text = $"匯入失敗：{ex.Message}";
            return;
        }

        PosStatusText.Text = $"匯入完成：新增 {inserted}、重複 {duplicate}、略過 {skipped}。";
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
                PosStatusText.Text = "對帳指定收銀機時必須指定設備 ID。";
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
        PosReconText.Text = $"對帳：總計 {summary.Total}、相符 {summary.Matched}、" +
                            $"未符 {summary.Unmatched}、重複 {summary.Duplicates}（候選事件 {candidates.Count}）。";
    }
}
