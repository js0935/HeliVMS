// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HeliVMS.Converters;

public class BoolToStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            string trueText, falseText;
            if (parameter is string s && !string.IsNullOrEmpty(s))
            {
                var parts = s.Split(',');
                trueText = parts[0];
                falseText = parts.Length > 1 ? parts[1] : $"○ {trueText}";
            }
            else
            {
                trueText = "啟用";
                falseText = "停用";
            }

            if (targetType == typeof(Brush))
            {
                return b
                    ? new SolidColorBrush(Color.FromRgb(0, 200, 151))
                    : new SolidColorBrush(Color.FromRgb(144, 144, 144));
            }

            return b ? $"● {trueText}" : $"○ {falseText}";
        }
        return "未知";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
