using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using HeliVMS.App.Services;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 巡航排程視窗（M72，§47）：每通道單一套巡航 Plan；編輯名稱/啟用/每日時窗與預設點步驟後整存
/// （PatrolRepository.Save：同頻道自動覆寫）。儲存成功回報含步驟數供 harness 斷言。
/// </summary>
public partial class PatrolWindow : Window
{
    private readonly PatrolRepository _patrols;
    private readonly IReadOnlyList<ChannelItem> _channels;
    private readonly ObservableCollection<PresetRow> _steps = new();
    private readonly PatrolHostService? _host;

    /// <param name="store">資料來源。</param>
    /// <param name="host">
    /// 巡邏執行主機；給了才能在這裡看到真實執行狀態（巡航中／待命／失敗），
    /// 因為排程的實際推進發生在 host 的背景迴圈，不是這個視窗。
    /// </param>
    public PatrolWindow(SqliteStore store, PatrolHostService? host = null)
    {
        _patrols = new PatrolRepository(store);
        _host = host;
        InitializeComponent();
        ApplyI18n();

        // 明說時窗是本地時間。Host 傳給 PatrolController 的時鐘是 DateTime.Now，
        // 這是刻意的（早期版本傳 UTC，結果台灣的「09:00 巡邏」在 01:00 執行，
        // 而且看起來完全正常）。這行字讓操作員知道該對著牆上的時鐘填。
        PatrolWindowHintText.Text = string.Format(Localizer.T("Patrol.Hint"), TimeZoneInfo.Local.Id);

        _channels = new ChannelRepository(store).List()
            .Select(c => new ChannelItem(c.Id, string.Format(Localizer.T("Patrol.ChannelLabel"), c.Id, c.Name))).ToList();
        PatrolChannelCombo.ItemsSource = _channels;
        PatrolChannelCombo.DisplayMemberPath = "Label";
        PatrolChannelCombo.SelectedIndex = _channels.Count > 0 ? 0 : -1;

        PatrolPresetList.ItemsSource = _steps;
    }

    /// <summary>
    /// 套用目前語系到巡航視窗的固定文字（M57 §14.7）。XAML 已無硬編中文，文字集中於此，
    /// 由 WpfI18nContractTests 的棘輪釘住三語齊備。
    /// </summary>
    private void ApplyI18n()
    {
        Title = Localizer.T("Patrol.Title");
        DescriptionText.Text = Localizer.T("Patrol.Description");
        ChannelLabelText.Text = Localizer.T("Patrol.Channel");
        NameLabelText.Text = Localizer.T("Patrol.Name");
        PatrolEnabledCheck.Content = Localizer.T("Patrol.Enable");
        WindowLabelText.Text = Localizer.T("Patrol.Window");
        PresetNameLabelText.Text = Localizer.T("Patrol.PresetName");
        DwellLabelText.Text = Localizer.T("Patrol.Dwell");
        PatrolAddPresetButton.Content = Localizer.T("Patrol.AddPreset");
        PatrolAddPresetButton.ToolTip = Localizer.T("Patrol.AddPresetTip");
        PatrolRemovePresetButton.Content = Localizer.T("Patrol.RemovePreset");
        PatrolRemovePresetButton.ToolTip = Localizer.T("Patrol.RemovePresetTip");
        PatrolColPresetName.Header = Localizer.T("Patrol.ColPresetName");
        PatrolColDwell.Header = Localizer.T("Patrol.ColDwell");
        PatrolSaveButton.Content = Localizer.T("Patrol.Save");
        PatrolSaveButton.ToolTip = Localizer.T("Patrol.SaveTip");
    }

    private sealed record ChannelItem(int Id, string Label);

    private sealed record PresetRow(int Seq, string PresetName, int DwellSeconds);

    private void OnChannelChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PatrolChannelCombo.SelectedItem is not ChannelItem channel)
        {
            return;
        }

        LoadPlan(channel.Id);
    }

    private void LoadPlan(int channelId)
    {
        _steps.Clear();
        var plan = _patrols.GetByChannel(channelId);
        if (plan is null)
        {
            PatrolNameBox.Text = Localizer.T("Patrol.DefaultName");
            PatrolEnabledCheck.IsChecked = false;
            PatrolStartBox.Text = "00:00";
            PatrolEndBox.Text = "23:59";
            PatrolStatusText.Text = Localizer.T("Patrol.NoPlan");
            return;
        }

        PatrolNameBox.Text = plan.Name;
        PatrolEnabledCheck.IsChecked = plan.Enabled;
        PatrolStartBox.Text = plan.WindowStart;
        PatrolEndBox.Text = plan.WindowEnd;
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var s = plan.Steps[i];
            _steps.Add(new PresetRow(i + 1, s.PresetName, s.DwellSeconds));
        }

        PatrolStatusText.Text = string.Format(Localizer.T("Patrol.Loaded"), plan.Steps.Count, plan.UpdatedAtUtc.ToString("yyyy-MM-dd HH:mm"));
    }

    private void OnAddPresetClicked(object sender, RoutedEventArgs e)
    {
        var name = PatrolPresetNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            PatrolStatusText.Text = Localizer.T("Patrol.NeedPresetName");
            return;
        }

        if (!int.TryParse(PatrolDwellBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dwell) ||
            dwell < 0)
        {
            PatrolStatusText.Text = Localizer.T("Patrol.BadDwell");
            return;
        }

        _steps.Add(new PresetRow(_steps.Count + 1, name, dwell));
        PatrolPresetNameBox.Clear();
        PatrolDwellBox.Text = "10";
        PatrolStatusText.Text = string.Format(Localizer.T("Patrol.Added"), name, dwell, _steps.Count);
    }

    private void OnRemovePresetClicked(object sender, RoutedEventArgs e)
    {
        if (PatrolPresetList.SelectedItem is not PresetRow row)
        {
            PatrolStatusText.Text = Localizer.T("Patrol.NeedSelect");
            return;
        }

        _steps.Remove(row);
        for (var i = 0; i < _steps.Count; i++)
        {
            _steps[i] = new PresetRow(i + 1, _steps[i].PresetName, _steps[i].DwellSeconds);
        }

        PatrolStatusText.Text = string.Format(Localizer.T("Patrol.Removed"), row.PresetName, _steps.Count);
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PatrolChannelCombo.SelectedItem is not ChannelItem channel)
            {
                PatrolStatusText.Text = Localizer.T("Patrol.NeedChannel");
                return;
            }

            // 時窗在存檔時就擋掉壞值。Host 端的退避是「整段視為全天」，
            // 那個退避是為了讓已經存進去的壞值不會讓攝影機整天不動；
            // 但對著這個視窗打字的操作員，我們可以立刻告訴他哪一格不對，
            // 否則存完畫面看起來一切正常，實際上全天都在轉頭。
            if (!PatrolWindowTime.TryParse(PatrolStartBox.Text, out var start))
            {
                PatrolStatusText.Text = string.Format(Localizer.T("Patrol.BadStart"), PatrolStartBox.Text);
                return;
            }

            if (!PatrolWindowTime.TryParse(PatrolEndBox.Text, out var end))
            {
                PatrolStatusText.Text = string.Format(Localizer.T("Patrol.BadEnd"), PatrolEndBox.Text);
                return;
            }

            if (end == start)
            {
                // PatrolController 的 InsideWindow 對 Start==End 是「只在那個整點分鐘」，
                // 控制器不會報錯，也不會有訊息——排程看起來正常，攝影機就是不會動。
                PatrolStatusText.Text = Localizer.T("Patrol.SameWindow");
                return;
            }

            var steps = _steps
                .Select(s => new PatrolStepRow(s.PresetName, s.DwellSeconds))
                .ToList();
            var updatedAt = DateTime.UtcNow;
            var id = _patrols.Save(
                null,
                PatrolNameBox.Text.Trim(),
                channel.Id,
                PatrolEnabledCheck.IsChecked == true,
                PatrolStartBox.Text.Trim(),
                PatrolEndBox.Text.Trim(),
                updatedAt,
                steps);

            var enabled = PatrolEnabledCheck.IsChecked == true;
            PatrolStatusText.Text = string.Format(
                Localizer.T("Patrol.Saved"),
                id,
                steps.Count,
                enabled ? Localizer.T("Patrol.Enabled") : Localizer.T("Patrol.Disabled"));

            // 提示而不是承諾：host 的下一次心跳才會看到 updated_at 變化而重建控制器，
            // 第一次 MoveTo 還要連 ONVIF 拉 profile。這裡如果寫「巡航已開始」就是又在騙人。
            if (enabled && _host is not null)
            {
                PatrolStatusText.Text += Localizer.T("Patrol.HostAccepted");
            }
        }
        catch (ArgumentException ex)
        {
            PatrolStatusText.Text = string.Format(Localizer.T("Patrol.SaveFailed"), ex.Message);
        }
    }
}