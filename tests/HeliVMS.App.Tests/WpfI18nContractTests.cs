using System.Text.RegularExpressions;
using HeliVMS.Storage;

namespace HeliVMS.App.Tests;

/// <summary>
/// WPF 視窗多語系棘輪（M57 §14.7）。逐窗在地化時，把 <c>(名稱, XAML, 程式碼, 鍵前綴)</c>
/// 加進 <see cref="Windows"/>，這三條就一併守住：畫面不再硬編中文、載入時呼叫
/// <c>ApplyI18n</c>、程式碼引用的鍵在每個語言都有翻譯。缺一即紅。
/// </summary>
public sealed class WpfI18nContractTests
{
    public static IEnumerable<object[]> Windows =>
    [
        ["SigningKeysWindow", "src/HeliVMS.App/SigningKeysWindow.xaml", "src/HeliVMS.App/SigningKeysWindow.xaml.cs", new[] { "Signing" }],
        ["MapWindow", "src/HeliVMS.App/MapWindow.xaml", "src/HeliVMS.App/MapWindow.xaml.cs", new[] { "Map" }],
        ["NotificationLogWindow", "src/HeliVMS.App/NotificationLogWindow.xaml", "src/HeliVMS.App/NotificationLogWindow.xaml.cs", new[] { "NotifyLog" }],
        ["AuditLogWindow", "src/HeliVMS.App/AuditLogWindow.xaml", "src/HeliVMS.App/AuditLogWindow.xaml.cs", new[] { "Audit" }],
        ["LegalHoldWindow", "src/HeliVMS.App/LegalHoldWindow.xaml", "src/HeliVMS.App/LegalHoldWindow.xaml.cs", new[] { "LegalHold" }],
        ["EvidenceWindow", "src/HeliVMS.App/EvidenceWindow.xaml", "src/HeliVMS.App/EvidenceWindow.xaml.cs", new[] { "Evidence" }],
        ["IoWindow", "src/HeliVMS.App/IoWindow.xaml", "src/HeliVMS.App/IoWindow.xaml.cs", new[] { "Io" }],
        ["RedactionWindow", "src/HeliVMS.App/RedactionWindow.xaml", "src/HeliVMS.App/RedactionWindow.xaml.cs", new[] { "Redact" }],
        ["DewarpWindow", "src/HeliVMS.App/DewarpWindow.xaml", "src/HeliVMS.App/DewarpWindow.xaml.cs", new[] { "Dewarp" }],
        ["AudioWindow", "src/HeliVMS.App/AudioWindow.xaml", "src/HeliVMS.App/AudioWindow.xaml.cs", new[] { "Audio" }],
        ["EventCenterWindow", "src/HeliVMS.App/EventCenterWindow.xaml", "src/HeliVMS.App/EventCenterWindow.xaml.cs", new[] { "EventCenter" }],
        ["SynopsisWindow", "src/HeliVMS.App/SynopsisWindow.xaml", "src/HeliVMS.App/SynopsisWindow.xaml.cs", new[] { "Synopsis" }],
        ["SnapshotRedactWindow", "src/HeliVMS.App/SnapshotRedactWindow.xaml", "src/HeliVMS.App/SnapshotRedactWindow.xaml.cs", new[] { "SnapRedact" }],
        ["ExitChoiceWindow", "src/HeliVMS.App/ExitChoiceWindow.xaml", "src/HeliVMS.App/ExitChoiceWindow.xaml.cs", new[] { "Exit" }],
        ["MainWindow", "src/HeliVMS.App/MainWindow.xaml", "src/HeliVMS.App/MainWindow.xaml.cs", new[] { "Main" }],
        ["LoginWindow", "src/HeliVMS.App/LoginWindow.xaml", "src/HeliVMS.App/LoginWindow.xaml.cs", new[] { "Login" }],
        ["DetectionWindow", "src/HeliVMS.App/DetectionWindow.xaml", "src/HeliVMS.App/DetectionWindow.xaml.cs", new[] { "Detection" }],
        ["ChannelDiagnosticsWindow", "src/HeliVMS.App/ChannelDiagnosticsWindow.xaml", "src/HeliVMS.App/ChannelDiagnosticsWindow.xaml.cs", new[] { "Diag" }],
        ["FailoverWindow", "src/HeliVMS.App/FailoverWindow.xaml", "src/HeliVMS.App/FailoverWindow.xaml.cs", new[] { "Failover" }],
        ["StreamSwitchWindow", "src/HeliVMS.App/StreamSwitchWindow.xaml", "src/HeliVMS.App/StreamSwitchWindow.xaml.cs", new[] { "Stream" }],
        ["PatrolWindow", "src/HeliVMS.App/PatrolWindow.xaml", "src/HeliVMS.App/PatrolWindow.xaml.cs", new[] { "Patrol" }],
        ["SearchWindow", "src/HeliVMS.App/SearchWindow.xaml", "src/HeliVMS.App/SearchWindow.xaml.cs", new[] { "Search" }],
        ["ShareWindow", "src/HeliVMS.App/ShareWindow.xaml", "src/HeliVMS.App/ShareWindow.xaml.cs", new[] { "Share" }],
        ["AnalyticsWindow", "src/HeliVMS.App/AnalyticsWindow.xaml", "src/HeliVMS.App/AnalyticsWindow.xaml.cs", new[] { "Analytics" }],
        ["EdgeBackfillWindow", "src/HeliVMS.App/EdgeBackfillWindow.xaml", "src/HeliVMS.App/EdgeBackfillWindow.xaml.cs", new[] { "Edge" }],
        ["SmartwallWindow", "src/HeliVMS.App/SmartwallWindow.xaml", "src/HeliVMS.App/SmartwallWindow.xaml.cs", new[] { "Smartwall" }],
        ["RulesWindow", "src/HeliVMS.App/RulesWindow.xaml", "src/HeliVMS.App/RulesWindow.xaml.cs", new[] { "Rules" }],
        ["ExportWindow", "src/HeliVMS.App/ExportWindow.xaml", "src/HeliVMS.App/ExportWindow.xaml.cs", new[] { "Export" }],
        ["SchedulingWindow", "src/HeliVMS.App/SchedulingWindow.xaml", "src/HeliVMS.App/SchedulingWindow.xaml.cs", new[] { "Schedule" }],
        ["PlaybackWindow", "src/HeliVMS.App/PlaybackWindow.xaml", "src/HeliVMS.App/PlaybackWindow.xaml.cs", new[] { "Playback" }],
        ["ExportCenterWindow", "src/HeliVMS.App/ExportCenterWindow.xaml", "src/HeliVMS.App/ExportCenterWindow.xaml.cs", new[] { "ExportCenter" }],
        ["OnvifWizardWindow", "src/HeliVMS.App/OnvifWizardWindow.xaml", "src/HeliVMS.App/OnvifWizardWindow.xaml.cs", new[] { "Onvif" }],
        ["CameraManagerWindow", "src/HeliVMS.App/CameraManagerWindow.xaml", "src/HeliVMS.App/CameraManagerWindow.xaml.cs", new[] { "Camera" }],
        ["DeviceToolsWindow", "src/HeliVMS.App/DeviceToolsWindow.xaml", "src/HeliVMS.App/DeviceToolsWindow.xaml.cs", new[] { "DeviceTools" }],
        ["FirmwareUpgradeWindow", "src/HeliVMS.App/FirmwareUpgradeWindow.xaml", "src/HeliVMS.App/FirmwareUpgradeWindow.xaml.cs", new[] { "Firmware" }],
        ["PtzWindow", "src/HeliVMS.App/PtzWindow.xaml", "src/HeliVMS.App/PtzWindow.xaml.cs", new[] { "Ptz" }],
        ["ChannelManagerWindow", "src/HeliVMS.App/ChannelManagerWindow.xaml", "src/HeliVMS.App/ChannelManagerWindow.xaml.cs", new[] { "Channels" }],
        ["MonitorGroupWindow", "src/HeliVMS.App/MonitorGroupWindow.xaml", "src/HeliVMS.App/MonitorGroupWindow.xaml.cs", new[] { "MonitorGroup", "Channels" }],
        ["TalkWindow", "src/HeliVMS.App/TalkWindow.xaml", "src/HeliVMS.App/TalkWindow.xaml.cs", new[] { "Talk" }],
        ["SettingsWindow", "src/HeliVMS.App/SettingsWindow.xaml", "src/HeliVMS.App/SettingsWindow.xaml.cs", new[] { "Settings" }],
    ];

    [Theory]
    [MemberData(nameof(Windows))]
    public void 視窗畫面不再硬編中文(string name, string xaml, string code, string[] prefixes)
    {
        _ = name;
        _ = code;
        _ = prefixes;
        Assert.DoesNotMatch(@"[\u4e00-\u9fff]", SourceContract.Read(xaml));
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void 視窗載入時套用多語系(string name, string xaml, string code, string[] prefixes)
    {
        _ = name;
        _ = xaml;
        var source = SourceContract.Read(code);
        Assert.Contains($"\"{prefixes[0]}.Title\"", source);
        Assert.Matches(@"ApplyI18n\s*\([^)]*\)\s*;", source);
    }

    [Theory]
    [MemberData(nameof(Windows))]
    public void 視窗每個多語系鍵都具備三語(string name, string xaml, string code, string[] prefixes)
    {
        _ = name;
        _ = xaml;
        var source = SourceContract.Read(code);

        var keys = prefixes
            .SelectMany(prefix => Regex.Matches(source, $"\"({prefix}\\.[A-Za-z0-9]+)\"")
                .Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(keys);
        foreach (var lang in I18n.Languages)
        {
            foreach (var key in keys)
            {
                Assert.NotEqual(key, I18n.Get(lang, key));
            }
        }
    }
}
