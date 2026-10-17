using System.Windows;
using System.Windows.Controls;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.App;

/// <summary>
/// 監看群組（監控中心頁簽）管理視窗。
///
/// <para>
/// 群組＝監控中心的一個頁簽：第一個頁簽固定顯示全部攝影機，其餘頁簽各顯示一個群組的自選成員。
/// 這裡只負責「群組↔頻道」的歸屬與群組內排序；頁簽實際如何把成員套用到監看牆（挑成員、
/// 只改格位不重連）由 <see cref="MainWindow"/> 依
/// <see cref="ChannelGroupRepository.ListChannelIds"/> + <c>ChannelLayout.SelectInOrder</c> 完成。
/// </para>
///
/// <para>
/// 這是從 <see cref="ChannelManagerWindow"/> 抽出的獨立功能：入口在設定中心的「裝置與頻道」頁，
/// 不再與頻道管理綁在一起。契約測試 <c>ChannelGroupContractTests</c> 掃描本檔的按鈕接線、
/// 管理員閘門與變更通知，改名或改接線前務必同步更新該測試。
/// </para>
/// </summary>
public partial class MonitorGroupWindow : Window
{
    private sealed record MemberRow(int Id, string Name);

    /// <summary>來源清單列：<paramref name="Label"/> 已附上「（群組：…）」標示所屬群組，供顯示；<paramref name="Name"/> 保留原頻道名供提示訊息使用。</summary>
    private sealed record SourceRow(int Id, string Name, string Label);

    private readonly ChannelRepository _channels;
    private readonly ChannelGroupRepository _groups;
    private readonly Action? _channelsChanged;

    private long? _selectedGroupId;
    private bool _groupDeleteArm;

    public MonitorGroupWindow(SqliteStore store, Action? channelsChanged = null)
    {
        _channels = new ChannelRepository(store);
        _groups = new ChannelGroupRepository(store);
        // 群組異動後通知主視窗立即重載（否則監看牆的頁簽與成員會停在舊狀態）。
        _channelsChanged = channelsChanged;

        InitializeComponent();
        ApplyI18n();
        ReloadSourceChannels();
        ReloadGroups();
    }

    /// <summary>群組異動後呼叫，請主視窗重載頁簽與成員。</summary>
    private void NotifyChannelsChanged() => _channelsChanged?.Invoke();

    private void ApplyI18n()
    {
        Title = Localizer.T("MonitorGroup.Title");
        MonitorGroupHeader.Text = Localizer.T("Channels.GroupHeader");
        MonitorGroupDesc.Text = Localizer.T("Channels.GroupDesc");
        SourceHeader.Text = Localizer.T("MonitorGroup.SourceHeader");
        GroupNameLabel.Text = Localizer.T("Channels.GroupNameLabel");
        GroupNameBox.ToolTip = Localizer.T("Channels.GroupNameTip");
        CreateGroupButton.Content = Localizer.T("Channels.GroupCreate");
        CreateGroupButton.ToolTip = Localizer.T("Channels.GroupCreateTip");
        RenameGroupButton.Content = Localizer.T("Channels.GroupRename");
        RenameGroupButton.ToolTip = Localizer.T("Channels.GroupRenameTip");
        DeleteGroupButton.Content = Localizer.T("Channels.GroupDelete");
        DeleteGroupButton.ToolTip = Localizer.T("Channels.GroupDeleteTip");
        GroupListHeader.Text = Localizer.T("Channels.GroupListHeader");
        GroupMembersHeader.Text = Localizer.T("Channels.GroupMembersHeader");
        AddToGroupButton.Content = Localizer.T("Channels.GroupAdd");
        AddToGroupButton.ToolTip = Localizer.T("Channels.GroupAddTip");
        RemoveFromGroupButton.Content = Localizer.T("Channels.GroupRemove");
        RemoveFromGroupButton.ToolTip = Localizer.T("Channels.GroupRemoveTip");
        GroupUpButton.Content = Localizer.T("Channels.GroupUp");
        GroupUpButton.ToolTip = Localizer.T("Channels.GroupUpTip");
        GroupDownButton.Content = Localizer.T("Channels.GroupDown");
        GroupDownButton.ToolTip = Localizer.T("Channels.GroupDownTip");
        GroupHintText.Text = Localizer.T("Channels.GroupSelectGroup");
        CloseButton.Content = Localizer.T("Channels.Close");
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 載入可加入群組的頻道來源清單（全部頻道）。
    /// 已屬於某群組的頻道在名稱後標示「（群組：…）」，讓操作者一眼看出它已被佔用；
    /// 一台攝影機只能屬於一個群組，故加入前要先確認它尚未歸屬。
    /// </summary>
    private void ReloadSourceChannels()
    {
        var groupNames = _groups.List().ToDictionary(g => g.Id, g => g.Name);
        var assignedGroup = new Dictionary<int, string>();
        foreach (var group in groupNames)
        {
            foreach (var channelId in _groups.ListChannelIds(group.Key))
            {
                assignedGroup[channelId] = group.Value;
            }
        }

        SourceChannelList.ItemsSource = _channels.List()
            .Select(c => new SourceRow(
                c.Id,
                c.Name,
                assignedGroup.TryGetValue(c.Id, out var groupName)
                    ? string.Format(Localizer.T("Channels.GroupAssignedLabel"), c.Name, groupName)
                    : c.Name))
            .ToList();
    }

    /// <summary>重建群組清單（可指定重新選取的群組）；同時歸零刪除確認與成員清單。</summary>
    private void ReloadGroups(long? reselect = null)
    {
        var groups = _groups.List();
        var keep = reselect ?? _selectedGroupId;

        // 先歸零：換 ItemsSource 會觸發 SelectionChanged，讓半更新狀態先清掉。
        _selectedGroupId = null;
        _groupDeleteArm = false;
        DeleteGroupButton.Content = Localizer.T("Channels.GroupDelete");
        GroupMemberList.ItemsSource = null;

        GroupList.ItemsSource = groups;

        var match = keep is long id ? groups.FirstOrDefault(g => g.Id == id) : null;
        if (match is not null)
        {
            // 設回選取會觸發 OnGroupSelected 載入成員。
            GroupList.SelectedItem = match;
        }
        else
        {
            GroupHintText.Text = Localizer.T("Channels.GroupSelectGroup");
        }
    }

    /// <summary>選取群組時載入其成員（依群組內順序）並預填名稱供改名。</summary>
    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        _groupDeleteArm = false;
        DeleteGroupButton.Content = Localizer.T("Channels.GroupDelete");

        if (GroupList.SelectedItem is not ChannelGroup group)
        {
            _selectedGroupId = null;
            GroupMemberList.ItemsSource = null;
            return;
        }

        _selectedGroupId = group.Id;
        GroupNameBox.Text = group.Name;
        ReloadMembers();
    }

    /// <summary>
    /// 載入目前群組的成員（依群組內順序）；名稱取自頻道清單。
    /// 頻道若已被刪除，資料層 CASCADE 已清掉歸屬，這裡自然不會出現孤兒列。
    /// </summary>
    private void ReloadMembers()
    {
        if (_selectedGroupId is not long groupId)
        {
            GroupMemberList.ItemsSource = null;
            return;
        }

        var names = _channels.List().ToDictionary(c => c.Id, c => c.Name);
        var members = _groups.ListChannelIds(groupId)
            .Select(id => new MemberRow(id, names.TryGetValue(id, out var name) ? name : $"#{id}"))
            .ToList();
        GroupMemberList.ItemsSource = members;

        GroupHintText.Text = members.Count == 0
            ? Localizer.T("Channels.GroupNoMembers")
            : string.Format(Localizer.T("Channels.GroupMemberCount"), members.Count);
    }

    private void OnCreateGroupClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupAdminOnly");
            return;
        }

        var name = GroupNameBox.Text.Trim();
        if (name.Length == 0)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupNameRequired");
            return;
        }

        try
        {
            var id = _groups.Create(name);
            GroupNameBox.Text = string.Empty;
            ReloadGroups(id);
            NotifyChannelsChanged();
            GroupHintText.Text = string.Format(Localizer.T("Channels.GroupCreated"), name);
        }
        catch (Exception ex)
        {
            GroupHintText.Text = string.Format(Localizer.T("Channels.GroupFailed"), ex.Message);
        }
    }

    private void OnRenameGroupClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupAdminOnly");
            return;
        }

        if (_selectedGroupId is not long id)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupSelectGroup");
            return;
        }

        var name = GroupNameBox.Text.Trim();
        if (name.Length == 0)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupNameRequired");
            return;
        }

        try
        {
            _groups.Rename(id, name);
            ReloadGroups(id);
            ReloadSourceChannels();
            NotifyChannelsChanged();
            GroupHintText.Text = string.Format(Localizer.T("Channels.GroupRenamed"), name);
        }
        catch (Exception ex)
        {
            GroupHintText.Text = string.Format(Localizer.T("Channels.GroupFailed"), ex.Message);
        }
    }

    private void OnDeleteGroupClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupAdminOnly");
            return;
        }

        if (_selectedGroupId is not long id || GroupList.SelectedItem is not ChannelGroup group)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupSelectGroup");
            return;
        }

        // 兩段式確認：刪除群組會連帶清掉成員歸屬（CASCADE），誤刪無法復原。
        if (!_groupDeleteArm)
        {
            _groupDeleteArm = true;
            DeleteGroupButton.Content = Localizer.T("Channels.GroupConfirmDelete");
            GroupHintText.Text = string.Format(Localizer.T("Channels.GroupDeleteConfirm"), group.Name);
            return;
        }

        try
        {
            _groups.Delete(id);
            _groupDeleteArm = false;
            DeleteGroupButton.Content = Localizer.T("Channels.GroupDelete");
            ReloadGroups();
            ReloadSourceChannels();
            NotifyChannelsChanged();
            GroupHintText.Text = string.Format(Localizer.T("Channels.GroupDeleted"), group.Name);
        }
        catch (Exception ex)
        {
            GroupHintText.Text = string.Format(Localizer.T("Channels.GroupFailed"), ex.Message);
        }
    }

    private void OnAddToGroupClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupAdminOnly");
            return;
        }

        if (_selectedGroupId is not long groupId)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupSelectGroup");
            return;
        }

        // 加入的來源是上方頻道清單目前選取的頻道；沒有選取就沒有東西可加入。
        if (SourceChannelList.SelectedItem is not SourceRow row)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupSelectChannel");
            return;
        }

        // 一台攝影機只能屬於一個群組：已在其他群組者一律擋下，要求先移除再加（不自動改派）。
        var owner = _groups.GroupOf(row.Id);
        if (owner is long ownerId)
        {
            var ownerName = _groups.Get(ownerId)?.Name ?? $"#{ownerId}";
            GroupHintText.Text = string.Format(Localizer.T("Channels.GroupAlreadyAssigned"), row.Name, ownerName);
            return;
        }

        try
        {
            _groups.Assign(row.Id, groupId);
            ReloadMembers();
            ReloadSourceChannels();
            NotifyChannelsChanged();
            GroupHintText.Text = string.Format(Localizer.T("Channels.GroupAdded"), row.Name);
        }
        catch (Exception ex)
        {
            GroupHintText.Text = string.Format(Localizer.T("Channels.GroupFailed"), ex.Message);
        }
    }

    private void OnRemoveFromGroupClicked(object sender, RoutedEventArgs e)
    {
        if (!SessionContext.IsAdmin)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupAdminOnly");
            return;
        }

        if (GroupMemberList.SelectedItem is not MemberRow member)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupSelectMember");
            return;
        }

        _groups.Remove(member.Id);
        ReloadMembers();
        ReloadSourceChannels();
        NotifyChannelsChanged();
        GroupHintText.Text = string.Format(Localizer.T("Channels.GroupRemoved"), member.Name);
    }

    private void OnGroupUpClicked(object sender, RoutedEventArgs e) => MoveGroupMember(-1);

    private void OnGroupDownClicked(object sender, RoutedEventArgs e) => MoveGroupMember(1);

    /// <summary>把選取的成員在群組內往前／後搬一格（負為前）；採搬移而非交換。</summary>
    private void MoveGroupMember(int delta)
    {
        if (!SessionContext.IsAdmin)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupAdminOnly");
            return;
        }

        if (_selectedGroupId is not long groupId)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupSelectGroup");
            return;
        }

        if (GroupMemberList.SelectedItem is not MemberRow member)
        {
            GroupHintText.Text = Localizer.T("Channels.GroupSelectMember");
            return;
        }

        _groups.Move(groupId, member.Id, delta);
        ReloadMembers();
        NotifyChannelsChanged();

        // 重載後重新選取同一台，讓連續點上／下移可以一路搬。
        if (GroupMemberList.ItemsSource is IEnumerable<MemberRow> rows)
        {
            var match = rows.FirstOrDefault(r => r.Id == member.Id);
            if (match is not null)
            {
                GroupMemberList.SelectedItem = match;
            }
        }

        GroupHintText.Text = string.Format(Localizer.T("Channels.GroupMoved"), member.Name);
    }
}
