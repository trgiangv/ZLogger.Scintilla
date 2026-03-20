using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
namespace ZLogger.Scintilla.Rendering;

internal static class LogPrefixParser
{
    /// <summary>
    /// Parses the <c>[timestamp LVL] </c> prefix from a UTF-8 byte span.
    /// </summary>
    public static ParsedPrefix TryParse(ReadOnlySpan<byte> utf8Line)
    {
        if (utf8Line.Length < 8 || utf8Line[0] != (byte)'[')
            return ParsedPrefix.NotFound;

        var closeIndex = -1;
        for (var i = 1; i + 1 < utf8Line.Length; i++)
        {
            if (utf8Line[i] != (byte) ']' || utf8Line[i + 1] != (byte) ' ') continue;
            closeIndex = i;
            break;
        }

        if (closeIndex <= 2)
            return ParsedPrefix.NotFound;

        var split = closeIndex - 4;
        if (split <= 1 || utf8Line[split] != (byte)' ')
            return ParsedPrefix.NotFound;

        var timestampLength = split - 1;

        return new ParsedPrefix(
            prefixByteLength: closeIndex + 2,
            timestampLength: timestampLength,
            levelByteStart: split + 1);
    }

    /// <summary>
    /// Parses the <c>[timestamp LVL] </c> prefix from a UTF-16 string.
    /// Returns the parse result and the remainder message after the prefix.
    /// </summary>
    public static ParsedPrefix TryParse(string message, out string remainderMessage)
    {
        remainderMessage = message;
        if (string.IsNullOrEmpty(message) || message[0] != '[')
            return ParsedPrefix.NotFound;

        var closeIndex = message.IndexOf("] ", StringComparison.Ordinal);
        if (closeIndex <= 4)
            return ParsedPrefix.NotFound;

        var split = -1;
        for (var i = closeIndex - 1; i >= 1; i--)
        {
            if (message[i] != ' ') continue;
            split = i;
            break;
        }

        if (split <= 1 || closeIndex - split != 4)
            return ParsedPrefix.NotFound;

        var timestampLength = split - 1;

        remainderMessage = closeIndex + 2 < message.Length
            ? message[(closeIndex + 2)..]
            : string.Empty;

        var prefixByteLength = GetUtf8ByteCount(message, 0, closeIndex + 2);

        return new ParsedPrefix(
            prefixByteLength: prefixByteLength,
            timestampLength: timestampLength,
            levelByteStart: split + 1);
    }

    /// <summary>
    /// Builds prefix <see cref="RenderSegment"/>s from a parsed UTF-8 prefix.
    /// </summary>
    public static void BuildPrefixSegments(
        ParsedPrefix result,
        ReadOnlySpan<byte> utf8Line,
        LogLevel fallbackLevel,
        IList<RenderSegment> segments)
    {
        var levelStyle = ParseShortLevel(
            utf8Line[result.LevelByteStart],
            utf8Line[result.LevelByteStart + 1],
            utf8Line[result.LevelByteStart + 2],
            fallbackLevel);

        BuildPrefixSegmentsCore(result.TimestampLength, 3, levelStyle, segments);

        var remainder = utf8Line.Length - result.PrefixByteLength;
        if (remainder > 0)
            segments.Add(new RenderSegment(remainder, SemanticStyle.Text));
    }

    /// <summary>
    /// Builds prefix <see cref="RenderSegment"/>s from a parsed string prefix.
    /// </summary>
    public static void BuildPrefixSegments(
        ParsedPrefix result,
        string message,
        LogLevel fallbackLevel,
        IList<RenderSegment> segments)
    {
        var levelStart = result.LevelByteStart;
        var levelStyle = levelStart >= 0 && levelStart + 2 < message.Length
            ? ParseShortLevel((byte)message[levelStart], (byte)message[levelStart + 1], (byte)message[levelStart + 2], fallbackLevel)
            : GetLevelSemanticStyle(fallbackLevel);

        var timestampByteLength = GetUtf8ByteCount(message, 1, result.TimestampLength);
        var levelByteLength = GetUtf8ByteCount(message, levelStart, 3);

        BuildPrefixSegmentsCore(timestampByteLength, levelByteLength, levelStyle, segments);
    }

    private static void BuildPrefixSegmentsCore(
        int timestampByteLength,
        int levelByteLength,
        SemanticStyle levelStyle,
        IList<RenderSegment> segments)
    {
        segments.Add(new RenderSegment(1, SemanticStyle.Punctuation));
        segments.Add(new RenderSegment(timestampByteLength, SemanticStyle.SecondaryText));
        segments.Add(new RenderSegment(1, SemanticStyle.SecondaryText));
        segments.Add(new RenderSegment(levelByteLength, levelStyle));
        segments.Add(new RenderSegment(2, SemanticStyle.Punctuation));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SemanticStyle ParseShortLevel(byte b0, byte b1, byte b2, LogLevel fallbackLevel) =>
        (b0, b1, b2) switch
        {
            ((byte)'T', (byte)'R', (byte)'C') => SemanticStyle.LevelTrace,
            ((byte)'D', (byte)'B', (byte)'G') => SemanticStyle.LevelDebug,
            ((byte)'I', (byte)'N', (byte)'F') => SemanticStyle.LevelInformation,
            ((byte)'W', (byte)'R', (byte)'N') => SemanticStyle.LevelWarning,
            ((byte)'E', (byte)'R', (byte)'R') => SemanticStyle.LevelError,
            ((byte)'C', (byte)'R', (byte)'T') => SemanticStyle.LevelCritical,
            _ => GetLevelSemanticStyle(fallbackLevel)
        };

    private static SemanticStyle GetLevelSemanticStyle(LogLevel level) => level switch
    {
        LogLevel.Trace => SemanticStyle.LevelTrace,
        LogLevel.Debug => SemanticStyle.LevelDebug,
        LogLevel.Information => SemanticStyle.LevelInformation,
        LogLevel.Warning => SemanticStyle.LevelWarning,
        LogLevel.Error => SemanticStyle.LevelError,
        LogLevel.Critical => SemanticStyle.LevelCritical,
        _ => SemanticStyle.LevelInformation
    };

    private static int GetUtf8ByteCount(string source, int start, int length)
    {
#if NET6_0_OR_GREATER
        return Encoding.UTF8.GetByteCount(source.AsSpan(start, length));
#else
        return Encoding.UTF8.GetByteCount(source.ToCharArray(), start, length);
#endif
    }
}
