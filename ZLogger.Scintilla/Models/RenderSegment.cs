namespace ZLogger.Scintilla.Models;

internal readonly struct RenderSegment
{
    internal RenderSegment(
        int utf8Length,
        SemanticStyle semanticStyle,
        TokenPayload? tokenPayload = null,
        bool isLink = false)
    {
        Utf8Length = utf8Length;
        SemanticStyle = semanticStyle;
        TokenPayload = tokenPayload;
        IsLink = isLink;
    }

    public int Utf8Length { get; }
    public SemanticStyle SemanticStyle { get; }
    internal TokenPayload? TokenPayload { get; }
    public bool IsLink { get; }
}
