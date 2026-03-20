using System.Drawing;
namespace ZLogger.Scintilla.Public;

/// <summary>
/// Defines the color contract for a Scintilla log viewer theme.
/// Implement this interface to provide a custom theme.
/// </summary>
public interface IScintillaTheme
{
    bool IsDark { get; }
    Color Background { get; }
    Color Foreground { get; }

    // Log level colors
    Color LevelTrace { get; }
    Color LevelDebug { get; }
    Color LevelInformation { get; }
    Color LevelWarning { get; }
    Color LevelError { get; }
    Color LevelCritical { get; }

    // Structural colors
    Color SecondaryText { get; }
    Color Punctuation { get; }

    // JSON token colors
    Color JsonKey { get; }
    Color JsonString { get; }
    Color JsonNumber { get; }
    Color JsonBoolean { get; }
    Color JsonNull { get; }
    Color JsonDateTime { get; }

    // Link / token colors
    Color LinkForeground { get; }
    Color SearchHighlight { get; }
}
