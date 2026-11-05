using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SteamCardCollector.App;

public sealed class AppColorConverter : IValueConverter
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x5C, 0x6E, 0x80),
        Color.FromRgb(0x6E, 0x4C, 0x72),
        Color.FromRgb(0x8C, 0x4E, 0x3A),
        Color.FromRgb(0x3E, 0x6B, 0x45),
        Color.FromRgb(0x4A, 0x52, 0x72),
        Color.FromRgb(0x6B, 0x58, 0x3A),
        Color.FromRgb(0x3A, 0x62, 0x70),
        Color.FromRgb(0x72, 0x3E, 0x48),
    ];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var id = value is uint appId ? appId : 0u;
        return BrushCache.Solid(Palette[id % (uint)Palette.Length]);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class FarmBadgeConverter : IMultiValueConverter
{
    private static readonly SolidColorBrush Row = BrushCache.Solid(Color.FromRgb(0x16, 0x20, 0x2D));
    private static readonly SolidColorBrush Active = BrushCache.Solid(Color.FromRgb(0x1A, 0x33, 0x48));
    private static readonly SolidColorBrush Cyan = BrushCache.Solid(Color.FromRgb(0x66, 0xC0, 0xF4));
    private static readonly SolidColorBrush Transparent = BrushCache.Solid(Colors.Transparent);

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var badge = Badge(values);
        return (parameter as string) switch
        {
            "bg" => badge == "OYNANIYOR" ? Active : Row,
            "border" => badge == "OYNANIYOR" ? Cyan : Transparent,
            _ => badge
        };
    }

    private static string Badge(object[] values)
    {
        if (values is not { Length: >= 3 } || values[0] is not uint appId || values[2] is not true)
            return "";
        return values[1] is uint current && current == appId ? "OYNANIYOR" : "SIRADA";
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal static class BrushCache
{
    private static readonly Dictionary<Color, SolidColorBrush> Cache = new();

    public static SolidColorBrush Solid(Color color)
    {
        if (Cache.TryGetValue(color, out var brush)) return brush;
        brush = new SolidColorBrush(color);
        brush.Freeze();
        Cache[color] = brush;
        return brush;
    }
}
