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

    Color LevelTrace { get; }
    Color LevelDebug { get; }
    Color LevelInformation { get; }
    Color LevelWarning { get; }
    Color LevelError { get; }
    Color LevelCritical { get; }

    Color SecondaryText { get; }
    Color Punctuation { get; }

    Color JsonKey { get; }
    Color JsonString { get; }
    Color JsonNumber { get; }
    Color JsonBoolean { get; }
    Color JsonNull { get; }
    Color JsonDateTime { get; }

    Color LinkForeground { get; }
    Color SearchHighlight { get; }
}
