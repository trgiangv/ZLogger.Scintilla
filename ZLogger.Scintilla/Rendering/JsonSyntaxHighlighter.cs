using System;
using System.Collections.Generic;
using ZLogger.Scintilla.Models;
// ReSharper disable LoopCanBeConvertedToQuery
namespace ZLogger.Scintilla.Rendering;

/// <summary>
/// Applies JSON syntax highlighting to already-formatted JSON text.
/// Does NOT perform serialization or pretty-printing — those are handled
/// by ZLogger's <c>SystemTextJsonZLoggerFormatter</c> at the processor level.
/// </summary>
internal sealed class JsonSyntaxHighlighter
{
    private readonly List<(int Start, int Length)> _urlMatchBuffer = new(LogConstants.UrlMatchBufferCapacity);

    public void AppendJsonStyledSegments(string text, IList<RenderSegment> segments)
    {
        var writer = new RenderTokenWriter(segments);
        var index = 0;
        while (index < text.Length)
        {
            if (RenderingChars.TryAppendWhitespace(text, writer, ref index) ||
                TryAppendJsonPunctuation(text, writer, ref index) ||
                TryAppendJsonQuotedString(text, writer, ref index) ||
                TryAppendJsonNumber(text, writer, ref index) ||
                TryAppendJsonKeyword(text, writer, ref index))
            {
                continue;
            }

            writer.Add(text, index, 1, SemanticStyle.Text);
            index++;
        }
    }

    private static bool TryAppendJsonPunctuation(string text, RenderTokenWriter writer, ref int index)
    {
        if (!RenderingChars.IsJsonPunctuation(text[index]))
            return false;

        writer.Add(text, index, 1, SemanticStyle.Punctuation);
        index++;
        return true;
    }

    private bool TryAppendJsonQuotedString(string text, RenderTokenWriter writer, ref int index)
    {
        if (text[index] != '"')
            return false;

        var start = index;
        index = FindQuotedStringEnd(text, index);
        var tokenLength = index - start;
        var style = IsJsonKeyString(text, index) ? SemanticStyle.JsonKey : SemanticStyle.JsonString;

        if (style == SemanticStyle.JsonString)
            AppendJsonStringWithLinks(text, start, tokenLength, writer);
        else
            writer.Add(text, start, tokenLength, style);

        return true;
    }

    private static bool TryAppendJsonNumber(string text, RenderTokenWriter writer, ref int index)
    {
        var ch = text[index];
        if (!char.IsDigit(ch) && ch != '-')
            return false;

        var start = index++;
        while (index < text.Length && (char.IsDigit(text[index]) || text[index] is '.' or 'e' or 'E' or '+' or '-'))
            index++;

        writer.Add(text, start, index - start, SemanticStyle.JsonNumber);
        return true;
    }

    private static bool TryAppendJsonKeyword(string text, RenderTokenWriter writer, ref int index)
    {
        if (MatchesWordAt(text, index, "true") || MatchesWordAt(text, index, "false"))
        {
            var length = text[index] == 't' ? 4 : 5;
            writer.Add(text, index, length, SemanticStyle.JsonBoolean);
            index += length;
            return true;
        }

        if (MatchesWordAt(text, index, "null"))
        {
            writer.Add("null", SemanticStyle.JsonNull);
            index += 4;
            return true;
        }

        return false;
    }

    private void AppendJsonStringWithLinks(string source, int start, int length, RenderTokenWriter writer)
    {
        if (length <= 2)
        {
            writer.Add(source, start, length, SemanticStyle.JsonString);
            return;
        }

        var contentStart = start + 1;
        var contentLength = length - 2;
        var contentSpan = source.AsSpan(contentStart, contentLength);
        if (!UrlScanner.HasPotentialCandidate(contentSpan) ||
            UrlScanner.FindAll(contentSpan, _urlMatchBuffer) == 0)
        {
            writer.Add(source, start, length, SemanticStyle.JsonString);
            return;
        }

        writer.Add(source, start, 1, SemanticStyle.JsonString);
        EmitUrlAwareSlice(source, contentStart, contentLength, writer);
        writer.Add(source, start + length - 1, 1, SemanticStyle.JsonString);
    }

    private void EmitUrlAwareSlice(string source, int sliceStart, int sliceLength, RenderTokenWriter writer)
    {
        var cursor = 0;
        foreach (var (matchStart, matchLength) in _urlMatchBuffer)
        {
            if (matchStart > cursor)
                writer.Add(source, sliceStart + cursor, matchStart - cursor, SemanticStyle.JsonString);

            UrlScanner.EmitUrlOrFallback(source, sliceStart + matchStart, matchLength, SemanticStyle.JsonString, writer, preserveStyle: true);
            cursor = matchStart + matchLength;
        }

        if (cursor < sliceLength)
            writer.Add(source, sliceStart + cursor, sliceLength - cursor, SemanticStyle.JsonString);
    }

    private static int FindQuotedStringEnd(string text, int startIndex)
    {
        var index = startIndex + 1;
        var escaped = false;
        while (index < text.Length)
        {
            var current = text[index++];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            if (current == '"')
                break;
        }

        return index;
    }

    private static bool IsJsonKeyString(string text, int tokenEnd)
    {
        var lookAhead = tokenEnd;
        while (lookAhead < text.Length && char.IsWhiteSpace(text[lookAhead]))
            lookAhead++;

        return lookAhead < text.Length && text[lookAhead] == ':';
    }

    private static bool MatchesWordAt(string text, int start, string word)
    {
        if (start < 0 || start + word.Length > text.Length)
            return false;

        for (var i = 0; i < word.Length; i++)
        {
            if (text[start + i] != word[i])
                return false;
        }

        return true;
    }
}
