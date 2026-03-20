using System.Drawing;
namespace ZLogger.Scintilla.Public;

/// <summary>
/// Built-in dark theme. Implements <see cref="IScintillaTheme"/>.
/// </summary>
internal sealed class DarkTheme : IScintillaTheme
{
    internal static readonly DarkTheme Instance = new();

    public bool IsDark => true;
    public Color Background { get; } = Color.FromArgb(37, 37, 37);
    public Color Foreground { get; } = Color.FromArgb(240, 240, 240);

    public Color LevelTrace { get; } = Color.FromArgb(140, 140, 140);
    public Color LevelDebug { get; } = Color.FromArgb(170, 170, 170);
    public Color LevelInformation { get; } = Color.FromArgb(120, 170, 255);
    public Color LevelWarning { get; } = Color.FromArgb(255, 220, 70);
    public Color LevelError { get; } = Color.FromArgb(255, 100, 100);
    public Color LevelCritical { get; } = Color.FromArgb(255, 80, 80);

    public Color SecondaryText { get; } = Color.FromArgb(180, 180, 180);
    public Color Punctuation { get; } = Color.FromArgb(160, 160, 160);

    public Color JsonKey { get; } = Color.FromArgb(255, 200, 255);
    public Color JsonString { get; } = Color.FromArgb(120, 255, 170);
    public Color JsonNumber { get; } = Color.FromArgb(255, 200, 120);
    public Color JsonBoolean { get; } = Color.FromArgb(170, 220, 255);
    public Color JsonNull { get; } = Color.FromArgb(180, 180, 255);
    public Color JsonDateTime { get; } = Color.FromArgb(120, 170, 255);

    public Color LinkForeground { get; } = Color.FromArgb(120, 170, 255);
    public Color SearchHighlight { get; } = Color.FromArgb(255, 213, 79);
}

/// <summary>
/// Built-in light theme. Implements <see cref="IScintillaTheme"/>.
/// </summary>
internal sealed class LightTheme : IScintillaTheme
{
    internal static readonly LightTheme Instance = new();

    public bool IsDark => false;
    public Color Background { get; } = Color.FromArgb(250, 250, 250);
    public Color Foreground { get; } = Color.FromArgb(40, 40, 40);

    public Color LevelTrace { get; } = Color.FromArgb(120, 120, 120);
    public Color LevelDebug { get; } = Color.FromArgb(80, 80, 80);
    public Color LevelInformation { get; } = Color.FromArgb(0, 80, 180);
    public Color LevelWarning { get; } = Color.FromArgb(200, 140, 0);
    public Color LevelError { get; } = Color.FromArgb(200, 60, 60);
    public Color LevelCritical { get; } = Color.FromArgb(150, 30, 30);

    public Color SecondaryText { get; } = Color.FromArgb(80, 80, 80);
    public Color Punctuation { get; } = Color.FromArgb(120, 120, 120);

    public Color JsonKey { get; } = Color.FromArgb(150, 0, 150);
    public Color JsonString { get; } = Color.FromArgb(0, 120, 0);
    public Color JsonNumber { get; } = Color.FromArgb(180, 80, 0);
    public Color JsonBoolean { get; } = Color.FromArgb(0, 80, 180);
    public Color JsonNull { get; } = Color.FromArgb(80, 80, 200);
    public Color JsonDateTime { get; } = Color.FromArgb(0, 80, 180);

    public Color LinkForeground { get; } = Color.FromArgb(0, 80, 180);
    public Color SearchHighlight { get; } = Color.FromArgb(0, 110, 220);
}

/// <summary>
/// Static factory for built-in themes.
/// </summary>
public static class ScintillaThemes
{
    /// <summary>Default dark theme.</summary>
    public static IScintillaTheme Dark => DarkTheme.Instance;

    /// <summary>Default light theme.</summary>
    public static IScintillaTheme Light => LightTheme.Instance;
}
