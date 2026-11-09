using ZLogger.Scintilla.Models;
namespace ZLogger.Scintilla.Rendering;

/// <summary>
/// Applies JSON syntax highlighting to already-formatted UTF-8 JSON.
/// Does NOT perform serialization or pretty-printing — those are handled
/// by <see cref="Pipeline.ScintillaLogProcessor"/> via LookupEngine decomposition.
/// </summary>
internal sealed class JsonSyntaxHighlighter
{
    private readonly List<(int Start, int Length)> _urls = new(LogConstants.UrlMatchBufferCapacity);

    public void AppendJsonStyledSegments(ReadOnlySpan<byte> text, IList<RenderSegment> segments)
    {
        var index = 0;
        while (index < text.Length)
            index = AppendToken(text, index, segments);
    }

    private int AppendToken(ReadOnlySpan<byte> text, int index, IList<RenderSegment> segments)
    {
        var value = text[index];
        switch (value)
        {
            case (byte)'{' or (byte)'}' or (byte)'[' or (byte)']' or (byte)',' or (byte)':':
                return AppendOne(index, SemanticStyle.Punctuation, segments);
            case (byte)'"':
                return AppendQuoted(text, index, segments);
            case (byte)'-':
                return AppendRun(text, index, IsNumberByte, SemanticStyle.JsonNumber, segments);
        }

        if (IsWhitespace(value))
            return AppendRun(text, index, IsWhitespace, SemanticStyle.SecondaryText, segments);
        return RenderingChars.IsAsciiDigit(value)
            ? AppendRun(text, index, IsNumberByte, SemanticStyle.JsonNumber, segments)
            : AppendKeyword(text, index, segments);
    }

    private static int AppendKeyword(ReadOnlySpan<byte> text, int index, IList<RenderSegment> segments)
    {
        var rest = text[index..];
        if (rest.StartsWith("true"u8))
            return AppendKeyword(index, 4, SemanticStyle.JsonBoolean, segments);
        if (rest.StartsWith("false"u8))
            return AppendKeyword(index, 5, SemanticStyle.JsonBoolean, segments);
        return rest.StartsWith("null"u8)
            ? AppendKeyword(index, 4, SemanticStyle.JsonNull, segments)
            : AppendOne(index, SemanticStyle.Text, segments);
    }

    private static int AppendKeyword(int index, int length, SemanticStyle style, IList<RenderSegment> segments)
    {
        segments.Add(new RenderSegment(length, style));
        return index + length;
    }

    private static int AppendOne(int index, SemanticStyle style, IList<RenderSegment> segments)
    {
        segments.Add(new RenderSegment(1, style));
        return index + 1;
    }

    private delegate bool BytePredicate(byte value);

    private static int AppendRun(ReadOnlySpan<byte> text, int index, BytePredicate belongs, SemanticStyle style, IList<RenderSegment> segments)
    {
        var end = index + 1;
        while (end < text.Length && belongs(text[end]))
            end++;

        segments.Add(new RenderSegment(end - index, style));
        return end;
    }

    private int AppendQuoted(ReadOnlySpan<byte> text, int start, IList<RenderSegment> segments)
    {
        var end = FindQuotedEnd(text, start);
        if (IsFollowedByColon(text, end))
            segments.Add(new RenderSegment(end - start, SemanticStyle.JsonKey));
        else
            AppendStringWithLinks(text.Slice(start, end - start), segments);
        return end;
    }

    private void AppendStringWithLinks(ReadOnlySpan<byte> quoted, IList<RenderSegment> segments)
    {
        UrlScanner.FindAll(quoted, _urls);

        var cursor = 0;
        foreach (var (start, length) in _urls)
        {
            if (!UrlScanner.TryCreateLink(quoted.Slice(start, length), out var link))
                continue;

            if (start > cursor)
                segments.Add(new RenderSegment(start - cursor, SemanticStyle.JsonString));
            segments.Add(new RenderSegment(length, SemanticStyle.JsonString, link, isLink: true));
            cursor = start + length;
        }

        if (cursor < quoted.Length)
            segments.Add(new RenderSegment(quoted.Length - cursor, SemanticStyle.JsonString));
    }

    private static int FindQuotedEnd(ReadOnlySpan<byte> text, int start)
    {
        var index = start + 1;
        while (index < text.Length)
        {
            var current = text[index++];
            switch (current)
            {
                case (byte)'\\':
                    index++;
                    break;
                case (byte)'"':
                    return index;
            }
        }

        return text.Length;
    }

    private static bool IsFollowedByColon(ReadOnlySpan<byte> text, int end)
    {
        var index = end;
        while (index < text.Length && IsWhitespace(text[index]))
            index++;

        return index < text.Length && text[index] == (byte)':';
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsNumberByte(byte value) =>
        RenderingChars.IsAsciiDigit(value) || value is (byte)'.' or (byte)'e' or (byte)'E' or (byte)'+' or (byte)'-';
}
