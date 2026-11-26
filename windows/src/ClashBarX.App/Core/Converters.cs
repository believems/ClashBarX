using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClashBarX.App.Core;

/// <summary>反转布尔到可见性（false → Visible；用于「非编辑态」容器）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible ? false : true;
}

/// <summary>布尔取反（true → false；用于「进行中不禁用」反向绑定）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}
