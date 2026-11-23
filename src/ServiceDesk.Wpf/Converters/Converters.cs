using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ServiceDesk.Domain;

namespace ServiceDesk.Wpf.Converters;

/// <summary>Maps status / priority / SLA state / grade enums to badge colours.</summary>
public sealed class BadgeBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new();

    private static SolidColorBrush B(string hex)
    {
        if (!Cache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Cache[hex] = brush;
        }
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TicketStatus s => s switch
        {
            TicketStatus.Queued => B("#64748B"),
            TicketStatus.Assigned => B("#0EA5E9"),
            TicketStatus.InProgress => B("#2563EB"),
            TicketStatus.Pending => B("#D97706"),
            TicketStatus.Escalated => B("#9333EA"),
            TicketStatus.Resolved => B("#16A34A"),
            TicketStatus.Closed => B("#15803D"),
            _ => B("#64748B")
        },
        TicketPriority p => p switch
        {
            TicketPriority.Critical => B("#DC2626"),
            TicketPriority.High => B("#EA580C"),
            TicketPriority.Medium => B("#CA8A04"),
            _ => B("#4B5563")
        },
        SlaState sla => sla switch
        {
            SlaState.OnTrack => B("#16A34A"),
            SlaState.AtRisk => B("#D97706"),
            SlaState.Breached => B("#DC2626"),
            SlaState.Met => B("#15803D"),
            _ => B("#64748B")
        },
        EvaluationGrade g => g switch
        {
            EvaluationGrade.Excellent => B("#15803D"),
            EvaluationGrade.Good => B("#2563EB"),
            EvaluationGrade.Fair => B("#CA8A04"),
            EvaluationGrade.Poor => B("#EA580C"),
            EvaluationGrade.SlaBreached => B("#DC2626"),
            _ => B("#64748B")
        },
        bool b => b ? B("#16A34A") : B("#DC2626"),
        _ => B("#64748B")
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Human-friendly text for domain enums.</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TicketStatus s => s.ToDisplay(),
        SlaState s => s.ToDisplay(),
        EvaluationGrade g => g.ToDisplay(),
        null => string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>UTC DateTime → local time string. Parameter = format (default "yyyy-MM-dd HH:mm").</summary>
public sealed class UtcToLocalConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var format = parameter as string ?? "yyyy-MM-dd HH:mm";
        return value is DateTime dt
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime().ToString(format, culture)
            : "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool → Visibility. ConverterParameter="Invert" flips the logic.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter as string == "Invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ (parameter as string == "Invert");
}

/// <summary>null / empty string → Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null && (value is not string s || !string.IsNullOrWhiteSpace(s));
        if (parameter as string == "Invert") hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Enum value ↔ RadioButton.IsChecked (ConverterParameter = enum member name).</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}
