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
