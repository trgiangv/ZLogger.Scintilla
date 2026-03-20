using System;
using System.Drawing;
namespace ZLogger.Scintilla.Models;

internal readonly struct TokenPayload
{
    private readonly Action? _onClick;

    public SemanticStyle SemanticStyle { get; }
    public Color? ColorizerForeground { get; }
    public bool IsLink => _onClick is not null;
    public bool HasColorizerColor => ColorizerForeground.HasValue;

    private TokenPayload(Action? onClick, SemanticStyle style, Color? colorizerForeground = null)
    {
        _onClick = onClick;
        SemanticStyle = style;
        ColorizerForeground = colorizerForeground;
    }

    public static TokenPayload ForLink(Action onClick)
        => new(onClick, SemanticStyle.TokenLink);

    public static TokenPayload ForColorizer(Color foreground)
        => new(null, SemanticStyle.TokenClassified, foreground);

    public void InvokeClick() => _onClick?.Invoke();
}
