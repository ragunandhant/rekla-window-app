using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RaceVideoProcessor.App.ViewModels;

namespace RaceVideoProcessor.App.Converters;

internal static class BrushLookup
{
    /// <summary>
    /// Resolves a palette brush by key. Converters never hard-code a colour so
    /// Theme/Palette.xaml stays the single source of truth.
    /// </summary>
    public static Brush Get(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static Brush Get(string key, bool soft) => Get(soft ? key + "Soft" : key);
}

/// <summary>Visible when the bound page equals the parameter page.</summary>
public sealed class PageToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AppPage page && parameter is string name &&
           string.Equals(page.ToString(), name, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True when the bound page equals the parameter page (navigation selection).</summary>
public sealed class PageToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AppPage page && parameter is string name &&
           string.Equals(page.ToString(), name, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True when the active list filter equals the parameter (filter chips).</summary>
public sealed class FilterToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is EntryFilter filter && parameter is string name &&
           string.Equals(filter.ToString(), name, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True when the bound value's name equals the parameter: category and upload chips.</summary>
public sealed class EqualsParameterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is string name &&
           string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Bool to visibility. Pass "invert" to reverse.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Visible when the string has content. Pass "invert" to show only when empty.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = !string.IsNullOrWhiteSpace(value as string);
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
            has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Entry status to palette brush. Pass "soft" for the badge background.</summary>
public sealed class EntryStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var soft = string.Equals(parameter as string, "soft", StringComparison.OrdinalIgnoreCase);
        var key = value is EntryDisplayStatus status
            ? status switch
            {
                EntryDisplayStatus.Completed or EntryDisplayStatus.HasVideo => "Success",
                EntryDisplayStatus.Processing or EntryDisplayStatus.Uploading or EntryDisplayStatus.Assigning => "Info",
                EntryDisplayStatus.Processed or EntryDisplayStatus.Ready => "Accent",
                EntryDisplayStatus.Failed or EntryDisplayStatus.UploadFailed or EntryDisplayStatus.AssignmentFailed
                    or EntryDisplayStatus.AuthenticationFailed or EntryDisplayStatus.VideoMissing => "Danger",
                EntryDisplayStatus.Outdated or EntryDisplayStatus.ExtractionPending or EntryDisplayStatus.Cancelled => "Warning",
                _ => "Neutral"
            }
            : "Neutral";

        return BrushLookup.Get(key, soft);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Banner severity to palette brush. Pass "soft" for the banner background.</summary>
public sealed class BannerKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var soft = string.Equals(parameter as string, "soft", StringComparison.OrdinalIgnoreCase);
        var key = value is BannerKind kind
            ? kind switch
            {
                BannerKind.Success => "Success",
                BannerKind.Warning => "Warning",
                BannerKind.Error => "Danger",
                _ => "Info"
            }
            : "Info";

        return BrushLookup.Get(key, soft);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Connection state to the status-dot colour.</summary>
public sealed class ConnectedToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BrushLookup.Get(value is bool connected && connected ? "Success" : "Danger");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Colours a log line by severity so errors stand out in the console view.</summary>
public sealed class LogLineToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var line = value as string ?? string.Empty;
        if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            return BrushLookup.Get("Danger");
        if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase))
            return BrushLookup.Get("Warning");
        return BrushLookup.Get("Text");
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
