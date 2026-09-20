using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SolventUI.Models;

namespace SolventUI.Converters;

// ---------------------------------------------------------------------
// All XAML value converters in one file — each is a couple of lines,
// so three separate files just added navigation overhead.
// ---------------------------------------------------------------------

public sealed class IssueSeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            IssueSeverity.Critical => "SolventErrorBrush",
            IssueSeverity.Warning => "SolventWarningBrush",
            IssueSeverity.Good => "SolventSuccessBrush",
            _ => "SolventInfoBrush"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LogKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            LogKind.Success => "SolventSuccessBrush",
            LogKind.Warning => "SolventWarningBrush",
            LogKind.Error => "SolventErrorBrush",
            _ => "SolventInfoBrush"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Null/empty string → Collapsed, anything else → Visible.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Negates a bool — e.g. binding a button's IsEnabled to "not busy" without a second view-model property.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>Looks up a theme brush by its resource key (e.g. "SolventSuccessBrush") — for a view model property that names a brush without referencing System.Windows.Media itself.</summary>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Converts between a DayOfWeek and its ComboBoxItem Tag string ("Monday", etc.) on the Schedule page.</summary>
public sealed class DayOfWeekToTagConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DayOfWeek day ? day.ToString() : "Monday";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && Enum.TryParse<DayOfWeek>(s, out var day) ? day : DayOfWeek.Monday;
}

/// <summary>Binds a RadioButton's IsChecked to one value of an enum or string property — ConverterParameter is the value this radio represents.</summary>
public sealed class EqualityToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true) return Binding.DoNothing;
        return targetType.IsEnum && parameter is string s ? Enum.Parse(targetType, s) : parameter;
    }
}

/// <summary>"#22C55E" style hex string → SolidColorBrush, for the Settings page's accent swatches.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string hex ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → a visible selection ring, false → none. Used for the Settings page's accent swatches.</summary>
public sealed class BoolToThicknessConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is true ? 3 : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Visible, false → Collapsed. Used for the Cleanup category list's "Caution" badge.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>CleanupCategory.IsCaution → the warning brush (true) or transparent (false), for the small risk pill background.</summary>
public sealed class BoolToRiskBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true
            ? Application.Current.TryFindResource("SolventWarningBrush") as Brush ?? Brushes.OrangeRed
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
