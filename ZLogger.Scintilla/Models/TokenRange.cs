namespace ZLogger.Scintilla.Models;

internal readonly struct TokenRange(int start, int length, TokenPayload payload)
{
    public int Start { get; } = start;
    public int Length { get; } = length;
    public TokenPayload Payload { get; } = payload;
    public TokenRange WithOffset(int delta) => new(Start + delta, Length, Payload);
}
