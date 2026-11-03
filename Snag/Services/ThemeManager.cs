using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Snag.Services;

/// <summary>Light, dark, or whatever Windows uses for apps.</summary>
public enum Appearance { Auto, Light, Dark }

/// <summary>
/// One look for Snag: light or dark controls, the window and card colours, and the accent gradient.
/// Every other theme colour (hovers, strokes, tracks…) is worked out from these.
/// </summary>
public sealed record ThemePalette(bool IsLight, Color Window, Color Surface, Color AccentStart, Color AccentEnd, Color Accent);

/// <summary>A colour theme, in its dark and light versions.</summary>
public sealed record ThemeColor(string Id, string Name, ThemePalette Dark, ThemePalette Light);

/// <summary>
/// Applies a theme by switching WPF's Fluent theme to light or dark and replacing the theme colours
/// declared in Theme.xaml (which the XAML uses as DynamicResource, so everything updates live).
/// <see cref="Appearance.Auto"/> follows Windows' light/dark app setting, including when it changes while Snag is open.
/// </summary>
public static partial class ThemeManager
{
    public const string DefaultThemeId = "violet";

    /// <summary>The custom colour being edited in Settings (saved presets have their own "preset-…" ids).</summary>
    public const string CustomId = "custom";

    [System.Text.RegularExpressions.GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial System.Text.RegularExpressions.Regex HexPattern();

    [System.Text.RegularExpressions.GeneratedRegex("^preset-[0-9a-f]{8}$")]
    private static partial System.Text.RegularExpressions.Regex PresetPattern();

    private static List<ThemeColor> _custom = [];

    public static IReadOnlyList<ThemeColor> Themes { get; } =
    [
        new("violet", "Violet",
            Dark("#202020", "#24242C", "#7C5CFF", "#C04BF2", "#A48BFF"),
            Light("#F3F3F6", "#FFFFFF", "#7C5CFF", "#C04BF2", "#6A4BE0")),
        new("crimson", "Crimson",
            Dark("#1B1012", "#2A171B", "#E0123F", "#FF5E62", "#FF7A8F"),
            Light("#FBF2F3", "#FFFFFF", "#D0103A", "#F2545B", "#B00E32")),
        new("ocean", "Ocean",
            Dark("#0B1722", "#132536", "#0284C7", "#14B8A6", "#4CC9F0"),
            Light("#EEF5F9", "#FFFFFF", "#0284C7", "#0D9488", "#0369A1")),
        new("forest", "Forest",
            Dark("#0F1712", "#17251C", "#15803D", "#84CC16", "#6EE7A0"),
            Light("#F0F6F1", "#FFFFFF", "#15803D", "#65A30D", "#166534")),
        new("sunset", "Sunset",
            Dark("#1C1310", "#2B1C17", "#F97316", "#EC4899", "#FFA06B"),
            Light("#FCF4EE", "#FFFFFF", "#EA580C", "#DB2777", "#C2410C")),
        new("sakura", "Sakura",
            Dark("#1D1218", "#2B1A24", "#EC4899", "#FB7185", "#F9A8D4"),
            Light("#FCF1F5", "#FFFFFF", "#EC4899", "#FB7185", "#BE185D")),
        // YouTube's own look: its red on near-black, or on white.
        new("youtube", "YouTube",
            Dark("#0F0F0F", "#212121", "#FF0000", "#FF2D55", "#FF4E45"),
            Light("#F9F9F9", "#FFFFFF", "#FF0000", "#FF2D55", "#CC0000")),
    ];

    private static bool _listening;

    public static string CurrentThemeId { get; private set; } = DefaultThemeId;
    public static Appearance CurrentAppearance { get; private set; } = Appearance.Dark;

    /// <summary>The palette actually showing (for Auto, the dark or light one).</summary>
    public static ThemePalette Current { get; private set; } = Themes[0].Dark;

    /// <summary>Raised on the UI thread after a theme has been applied.</summary>
    public static event Action? Changed;

    public static bool IsKnown(string? id) => id is not null && Find(id) is not null;

    public static bool IsPresetId(string? id) => id is not null && PresetPattern().IsMatch(id);

    /// <summary>The custom colour and saved presets (rebuilt whenever they change).</summary>
    public static void SetCustomThemes(IEnumerable<ThemeColor> themes) => _custom = themes.ToList();

    public static ThemeColor? Find(string id) => Themes.FirstOrDefault(t => t.Id == id) ?? _custom.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// A whole theme from one colour: the accent gradient runs from it to a neighbouring hue, and with
    /// <paramref name="tint"/> the window and cards take on a faint shade of it too.
    /// </summary>
    public static ThemeColor FromAccent(string id, string name, Color accent, bool tint)
    {
        var (h, s, l) = ToHsl(accent);
        var start = FromHsl(h, s, Math.Clamp(l, 0.38, 0.62));
        var end = FromHsl((h + 32) % 360, s, Math.Clamp(l + 0.06, 0.42, 0.68));
        var dark = new ThemePalette(false,
            tint ? FromHsl(h, s * 0.35, 0.08) : Hex("#202020"),
            tint ? FromHsl(h, s * 0.3, 0.13) : Hex("#24242C"),
            start, end, FromHsl(h, s, Math.Clamp(l, 0.72, 0.85)));
        var light = new ThemePalette(true,
            tint ? FromHsl(h, s * 0.5, 0.96) : Hex("#F3F3F6"),
            Colors.White,
            start, end, FromHsl(h, s, Math.Clamp(l, 0.22, 0.38)));
        return new ThemeColor(id, name, dark, light);
    }

    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (text is null || !HexPattern().IsMatch(text.Trim())) return false;
        color = Hex(text.Trim());
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double Channel(double n)
        {
            var k = (n + h / 30) % 12;
            var a = s * Math.Min(l, 1 - l);
            return l - a * Math.Max(-1, Math.Min(k - 3, Math.Min(9 - k, 1)));
        }
        return Color.FromRgb((byte)Math.Round(Channel(0) * 255), (byte)Math.Round(Channel(8) * 255), (byte)Math.Round(Channel(4) * 255));
    }

    public static void Apply(string themeId, Appearance appearance)
    {
        CurrentThemeId = IsKnown(themeId) ? themeId : DefaultThemeId;
        // (Re-applying the same theme is how an edited custom colour takes effect.)
        CurrentAppearance = appearance;
        var palette = Current = Resolve(CurrentThemeId, appearance);
        var app = Application.Current;

        // Fluent first: switching it reloads its own dictionaries, then our colours go on top.
        app.ThemeMode = palette.IsLight ? ThemeMode.Light : ThemeMode.Dark;
        foreach (var (key, value) in Resources(palette)) app.Resources[key] = value;

        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != UserPreferenceCategory.General) return;
                app.Dispatcher.BeginInvoke(() =>
                {
                    if (CurrentAppearance == Appearance.Auto && Resolve(CurrentThemeId, Appearance.Auto) != Current)
                        Apply(CurrentThemeId, Appearance.Auto);
                });
            };
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Video controls sit on a dark scrim over the picture in full screen, whatever the theme. This gives
    /// <paramref name="scope"/> the dark theme's text and track colours (or takes them away again).
    /// </summary>
    public static void UseDarkColors(ResourceDictionary scope, bool on)
    {
        var dark = Resources(Themes[0].Dark).ToDictionary(p => p.Key, p => p.Value);
        string[] keys = ["MutedBrush", "FaintBrush", "StrongTextBrush", "HoverBrush", "PressedBrush", "SliderTrackBrush", "KnobStrokeBrush"];
        foreach (var key in keys)
        {
            if (on) scope[key] = dark[key];
            else scope.Remove(key);
        }
        if (on)
        {
            scope["TextFillColorPrimaryBrush"] = Brushes.White;
            scope["TextFillColorSecondaryBrush"] = Solid(Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            scope.Remove("TextFillColorPrimaryBrush");
            scope.Remove("TextFillColorSecondaryBrush");
        }
    }

    private static ThemePalette Resolve(string themeId, Appearance appearance)
    {
        var theme = Find(themeId) ?? Themes[0];
        var light = appearance == Appearance.Light || (appearance == Appearance.Auto && WindowsUsesLightTheme());
        return light ? theme.Light : theme.Dark;
    }

    private static IEnumerable<(string Key, object Value)> Resources(ThemePalette p)
    {
        var light = p.IsLight;
        var ink = light ? Colors.Black : Colors.White; // hovers, strokes and tiles are tints of this
        var accent = Mix(p.AccentStart, p.Accent, 0.35);

        yield return ("WindowBrush", Solid(p.Window));
        yield return ("SurfaceBrush", Solid(p.Surface));
        yield return ("ToastBrush", Solid(Alpha(p.Surface, 0xF2)));
        yield return ("OverlayBrush", Solid(Alpha(light ? p.Window : Mix(p.Window, Colors.Black, 0.4), 0xD0)));
        yield return ("ScrimBrush", Solid(Color.FromArgb(light ? (byte)0x73 : (byte)0xB8, 0, 0, 0)));

        yield return ("BrandGradient", Gradient(p.AccentStart, p.AccentEnd, new Point(1, 1)));
        yield return ("BrandGradientHorizontal", Gradient(p.AccentStart, p.AccentEnd, new Point(1, 0)));
        yield return ("BrandBrush", Solid(p.Accent));
        yield return ("BrandSoftBrush", Solid(Alpha(p.AccentStart, light ? (byte)0x1F : (byte)0x2E)));
        yield return ("SelectedBrush", Solid(Alpha(accent, light ? (byte)0x1F : (byte)0x24)));
        yield return ("HaloBrush", Solid(Alpha(p.Accent, 0x33)));

        yield return ("DangerBrush", Solid(light ? Hex("#C42B1C") : Hex("#FF8080")));
        yield return ("SuccessBrush", Solid(light ? Hex("#107C41") : Hex("#5EE08F")));
        yield return ("StrongTextBrush", Solid(light ? Hex("#1B1B1F") : Colors.White));
        yield return ("MutedBrush", Solid(light ? Hex("#5E5E6E") : Hex("#A9A9B8")));
        yield return ("FaintBrush", Solid(light ? Hex("#8A8A99") : Hex("#7A7A8E")));

        yield return ("HoverBrush", Solid(Alpha(ink, light ? (byte)0x0D : (byte)0x14)));
        yield return ("PressedBrush", Solid(Alpha(ink, light ? (byte)0x08 : (byte)0x0A)));
        yield return ("ControlHoverBrush", Solid(Alpha(ink, light ? (byte)0x12 : (byte)0x1F)));
        yield return ("ControlPressedBrush", Solid(Alpha(ink, light ? (byte)0x08 : (byte)0x0D)));
        yield return ("NavCheckedBrush", Solid(light ? Colors.White : Alpha(Colors.White, 0x24)));
        yield return ("TileBrush", Solid(Alpha(ink, light ? (byte)0x0F : (byte)0x1A)));
        yield return ("StrokeBrush", Solid(Alpha(ink, light ? (byte)0x1A : (byte)0x1F)));
        yield return ("ChipBrush", Solid(Alpha(ink, light ? (byte)0x08 : (byte)0x0F)));
        yield return ("TrackBrush", Solid(Alpha(ink, light ? (byte)0x1A : (byte)0x1F)));
        yield return ("SliderTrackBrush", Solid(Alpha(ink, light ? (byte)0x24 : (byte)0x2E)));
        yield return ("SwitchBorderBrush", Solid(Alpha(ink, 0x8C)));
        yield return ("SwitchBorderHoverBrush", Solid(Alpha(ink, 0xCC)));
        yield return ("SwitchFillBrush", Solid(Alpha(ink, light ? (byte)0x06 : (byte)0x0A)));
        yield return ("SwitchKnobBrush", Solid(light ? Alpha(Colors.Black, 0xA6) : Alpha(Colors.White, 0xD9)));
        yield return ("KnobStrokeBrush", Solid(light ? Alpha(Colors.Black, 0x33) : Colors.Transparent));

        // The Fluent controls' own accent (checkboxes, focus rings, combo boxes). These keys in the
        // application's root resources win over the theme's.
        Color light1 = Mix(accent, Colors.White, 0.12), light2 = Mix(accent, Colors.White, 0.25), light3 = Mix(accent, Colors.White, 0.42);
        Color dark1 = Mix(accent, Colors.Black, 0.12), dark2 = Mix(accent, Colors.Black, 0.25), dark3 = Mix(accent, Colors.Black, 0.4);
        yield return ("SystemAccentColor", accent);
        yield return ("SystemAccentColorLight1", light1);
        yield return ("SystemAccentColorLight2", light2);
        yield return ("SystemAccentColorLight3", light3);
        yield return ("SystemAccentColorDark1", dark1);
        yield return ("SystemAccentColorDark2", dark2);
        yield return ("SystemAccentColorDark3", dark3);
        var fill = light ? dark1 : p.Accent;
        yield return ("AccentFillColorDefaultBrush", Solid(fill));
        yield return ("AccentFillColorSecondaryBrush", Solid(Alpha(fill, 0xE6)));
        yield return ("AccentFillColorTertiaryBrush", Solid(Alpha(fill, 0xCC)));
        yield return ("AccentTextFillColorPrimaryBrush", Solid(light ? dark2 : light3));
        yield return ("AccentTextFillColorSecondaryBrush", Solid(light ? dark2 : light3));
        yield return ("AccentTextFillColorTertiaryBrush", Solid(light ? dark1 : light2));
    }

    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static ThemePalette Dark(string window, string surface, string from, string to, string accent) =>
        new(false, Hex(window), Hex(surface), Hex(from), Hex(to), Hex(accent));

    private static ThemePalette Light(string window, string surface, string from, string to, string accent) =>
        new(true, Hex(window), Hex(surface), Hex(from), Hex(to), Hex(accent));

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static LinearGradientBrush Gradient(Color from, Color to, Point end)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), end);
        brush.Freeze();
        return brush;
    }

    private static Color Alpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
