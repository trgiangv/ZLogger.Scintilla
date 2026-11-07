using System.Drawing;
namespace ZLogger.Scintilla.Models;

/// <summary>
/// What a styled token carries beyond its style. A class rather than a struct: almost no range has one,
/// and every <see cref="StyleRange"/> would otherwise pay for it in size.
/// </summary>
internal sealed class TokenPayload
{
    private readonly Action? _onClick;

    public Color? ColorizerForeground { get; }
    public bool IsLink => _onClick is not null;
    public bool HasColorizerColor => ColorizerForeground.HasValue;

    private TokenPayload(Action? onClick, Color? colorizerForeground = null)
    {
        _onClick = onClick;
        ColorizerForeground = colorizerForeground;
    }

    public static TokenPayload ForLink(Action onClick)
        => new(onClick);

    public static TokenPayload ForColorizer(Color foreground)
        => new(null, foreground);

    public void InvokeClick() => _onClick?.Invoke();
}
