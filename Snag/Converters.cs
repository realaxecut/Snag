using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Snag;

/// <summary>
/// Truthy (true, non-empty, non-zero, non-null) → Visible. ConverterParameter "!" inverts.
/// Bindings through a property that can be null (Player.Current.X) never reach the converter
/// when it is null, so those also need FallbackValue=Collapsed.
/// </summary>
public sealed class VisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            double d => d != 0,
            _ => true,
        };
        if (parameter as string == "!") truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>value == parameter (by name), as a bool or a Visibility. Two-way for enum-backed radio buttons.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var equal = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        return targetType == typeof(Visibility) ? (equal ? Visibility.Visible : Visibility.Collapsed) : equal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>
/// Loads thumbnails: https images from the allowed YouTube/TikTok/Instagram image hosts, or Snag's
/// own local thumbnail copies. Anything else is ignored. Cached for scrolling lists.
/// </summary>
public sealed class ImageUrlConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string source) return null;
        var isLocal = Services.ThumbnailCache.IsCachedPath(source);
        if (isLocal ? !File.Exists(source) : !Services.UrlGuard.IsAllowedImageUrl(source)) return null;

        if (Cache.TryGetValue(source, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(source, UriKind.Absolute);
            image.DecodePixelWidth = 480;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            // Read local files fully up front so they're never left locked.
            if (isLocal) image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            if (Cache.Count > 500) Cache.Clear();
            Cache[source] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
