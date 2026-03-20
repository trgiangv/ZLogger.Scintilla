namespace ZLogger.Scintilla.Models;

internal readonly struct StyleRange(int length, int styleId, TokenPayload? tokenPayload, bool isLink)
{
    public int Length { get; } = length;
    public int StyleId { get; } = styleId;
    public TokenPayload? TokenPayload { get; } = tokenPayload;
    public bool IsLink { get; } = isLink;
}
