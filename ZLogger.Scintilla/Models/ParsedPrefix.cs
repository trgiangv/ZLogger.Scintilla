namespace ZLogger.Scintilla.Models;

internal readonly struct ParsedPrefix(int prefixByteLength, int timestampLength, int levelByteStart)
{
    public static ParsedPrefix NotFound => default;

    public bool Found { get; } = true;
    public int PrefixByteLength { get; } = prefixByteLength;
    public int TimestampLength { get; } = timestampLength;
    public int LevelByteStart { get; } = levelByteStart;
}
