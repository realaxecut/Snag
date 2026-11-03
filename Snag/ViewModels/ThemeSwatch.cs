using System.Windows;
using System.Windows.Media;
using Snag.Services;

namespace Snag.ViewModels;

public enum SwatchKind { BuiltIn, Preset, Custom }

/// <summary>
/// A colour theme as a swatch in Settings, drawn in whichever of light or dark is showing. The swatches
/// recolour themselves rather than being replaced, so the selected one stays selected across a mode switch.
/// </summary>
public sealed class ThemeSwatch(ThemeColor theme, SwatchKind kind) : ObservableObject
{
    public ThemeColor Theme
    {
        get;
        set
        {
            if (Set(ref field, value)) OnPropertyChanged(nameof(Name));
        }
    } = theme;

    public SwatchKind Kind { get; } = kind;
    public string Id => Theme.Id;
    public string Name => Theme.Name;
    public bool IsCustom => Kind == SwatchKind.Custom;

    public Brush? Window { get; private set => Set(ref field, value); }
    public Brush? Surface { get; private set => Set(ref field, value); }
    public Brush? Accent { get; private set => Set(ref field, value); }

    public void ShowAs(bool light)
    {
        var palette = light ? Theme.Light : Theme.Dark;
        Window = ThemeManager.Solid(palette.Window);
        Surface = ThemeManager.Solid(palette.Surface);
        Accent = ThemeManager.Gradient(palette.AccentStart, palette.AccentEnd, new Point(1, 1));
    }
}
