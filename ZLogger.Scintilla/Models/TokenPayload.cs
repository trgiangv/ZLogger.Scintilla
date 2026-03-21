using System;
using System.Drawing;
namespace ZLogger.Scintilla.Models;

internal readonly struct TokenPayload
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
