// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using HeliVMS.Models;

namespace HeliVMS.Converters;

public class RecordingConfigToStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var json = value as string;
        var active = IsRecordingActive(json);

        if (targetType == typeof(Brush))
            return active
                ? new SolidColorBrush(Color.FromRgb(0, 200, 151))
                : new SolidColorBrush(Color.FromRgb(144, 144, 144));

        return active ? "● 啟用" : "○ 停用";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool IsRecordingActive(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        var config = CameraRecordingConfigData.Deserialize(json);
        if (config is null) return false;
        return config.IsContinuousEnabled
            || config.IsAlarmEnabled
            || config.IsMotionEnabled
            || config.IsSmartEnabled;
    }
}
