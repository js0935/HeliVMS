using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
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

    public PatrolWindow(SqliteStore store)
    {
        _patrols = new PatrolRepository(store);
        InitializeComponent();

        _channels = new ChannelRepository(store).List()
            .Select(c => new ChannelItem(c.Id, $"頻道 {c.Id}（{c.Name}）")).ToList();
        PatrolChannelCombo.ItemsSource = _channels;
        PatrolChannelCombo.DisplayMemberPath = "Label";
        PatrolChannelCombo.SelectedIndex = _channels.Count > 0 ? 0 : -1;

        PatrolPresetList.ItemsSource = _steps;
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
            PatrolNameBox.Text = "巡航 1";
            PatrolEnabledCheck.IsChecked = false;
            PatrolStartBox.Text = "00:00";
            PatrolEndBox.Text = "23:59";
            PatrolStatusText.Text = "此頻道尚無巡航設定；填妥後按儲存。";
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

        PatrolStatusText.Text = $"已載入：{plan.Steps.Count} 個步驟（更新 {plan.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC）。";
    }

    private void OnAddPresetClicked(object sender, RoutedEventArgs e)
    {
        var name = PatrolPresetNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            PatrolStatusText.Text = "請輸入預設點名稱。";
            return;
        }

        if (!int.TryParse(PatrolDwellBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dwell) ||
            dwell < 0)
        {
            PatrolStatusText.Text = "停留秒數須為非負整數。";
            return;
        }

        _steps.Add(new PresetRow(_steps.Count + 1, name, dwell));
        PatrolPresetNameBox.Clear();
        PatrolDwellBox.Text = "10";
        PatrolStatusText.Text = $"已加入步驟：{name}（{dwell} 秒）；共 {_steps.Count} 步驟。";
    }

    private void OnRemovePresetClicked(object sender, RoutedEventArgs e)
    {
        if (PatrolPresetList.SelectedItem is not PresetRow row)
        {
            PatrolStatusText.Text = "請先選取要移除的步驟。";
            return;
        }

        _steps.Remove(row);
        for (var i = 0; i < _steps.Count; i++)
        {
            _steps[i] = new PresetRow(i + 1, _steps[i].PresetName, _steps[i].DwellSeconds);
        }

        PatrolStatusText.Text = $"已移除步驟 {row.PresetName}；共 {_steps.Count} 步驟。";
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PatrolChannelCombo.SelectedItem is not ChannelItem channel)
            {
                PatrolStatusText.Text = "尚未選擇頻道。";
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

            PatrolStatusText.Text = $"已儲存巡航（序號 {id}）：{steps.Count} 個步驟、{(PatrolEnabledCheck.IsChecked == true ? "已啟用" : "未啟用")}。";
        }
        catch (ArgumentException ex)
        {
            PatrolStatusText.Text = $"儲存失敗：{ex.Message}";
        }
    }
}