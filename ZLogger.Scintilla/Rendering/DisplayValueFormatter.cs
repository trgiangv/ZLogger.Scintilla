using System;
#if NET8_0_OR_GREATER
using System.Buffers;
#endif
using System.Collections.Generic;
using ZLogger.Scintilla.Models;
// ReSharper disable ForCanBeConvertedToForeach
namespace ZLogger.Scintilla.Rendering;

internal sealed class DisplayValueFormatter(TokenResolutionEngine tokenResolution)
{
#if NET8_0_OR_GREATER
    private static readonly SearchValues<char> TokenSeparators =
        SearchValues.Create(RenderingChars.TokenSeparators);
#endif
    private readonly List<(int Start, int Length)> _urlMatchBuffer = new(LogConstants.UrlMatchBufferCapacity);
    private readonly List<ParameterMatch> _paramSpanBuffer = new(4);

    public void AppendSegments(RenderContext context, string message, IList<RenderSegment> segments)
    {
        if (string.IsNullOrEmpty(message))
            return;

        var writer = new RenderTokenWriter(segments);
        AppendTokenAwareWithParameterSpans(context, message, writer);
    }

    private void AppendTokenAwareWithParameterSpans(RenderContext context, string message, RenderTokenWriter writer)
    {
        var paramMatches = CollectParameterMatchSpans(context, message);
        if (paramMatches.Count == 0)
        {
            AppendTokenAwareSegments(context, message, 0, message.Length, writer);
            return;
        }

        if (paramMatches.Count > 1)
            paramMatches.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var cursor = 0;
        foreach (var match in paramMatches)
        {
            if (match.Start < cursor)
                continue;

            if (match.Start > cursor)
                AppendTokenAwareSegments(context, message, cursor, match.Start - cursor, writer);

            writer.Add(message, match.Start, match.Length, match.Style);
            cursor = match.Start + match.Length;
        }
        if (cursor < message.Length)
            AppendTokenAwareSegments(context, message, cursor, message.Length - cursor, writer);
    }

    private void AppendTokenAwareSegments(RenderContext context, string message, int start, int length, RenderTokenWriter writer)
    {
        if (length <= 0)
            return;

        var span = message.AsSpan(start, length);
        if (!UrlScanner.HasPotentialCandidate(span) || UrlScanner.FindAll(span, _urlMatchBuffer) == 0)
        {
            AppendNonUrlTokenAwareSegments(context, message, start, length, writer);
            return;
        }

        var cursor = 0;
        for (var i = 0; i < _urlMatchBuffer.Count; i++)
        {
            var match = _urlMatchBuffer[i];
            if (match.Start > cursor)
                AppendNonUrlTokenAwareSegments(context, message, start + cursor, match.Start - cursor, writer);

            UrlScanner.EmitUrlOrFallback(message, start + match.Start, match.Length, SemanticStyle.Text, writer);
            cursor = match.Start + match.Length;
        }

        if (cursor < length)
            AppendNonUrlTokenAwareSegments(context, message, start + cursor, length - cursor, writer);
    }

    private List<ParameterMatch> CollectParameterMatchSpans(
        RenderContext context, string message)
    {
        _paramSpanBuffer.Clear();
        if (!context.Properties.TryGetValue(LogConstants.ParameterSpans, out var raw)
            || raw is not ParameterSpan[] spans)
            return _paramSpanBuffer;

        foreach (var ps in spans)
        {
            if (ps.SemanticStyle == SemanticStyle.Text)
                continue;

            ScanParameterOccurrences(message, ps, _paramSpanBuffer);
        }

        return _paramSpanBuffer;
    }

    private static void ScanParameterOccurrences(
        string message,
        ParameterSpan ps,
        List<ParameterMatch> output)
    {
        var searchIndex = 0;
        while (searchIndex < message.Length)
        {
            var found = message.IndexOf(ps.FormattedValue, searchIndex, StringComparison.Ordinal);
            if (found < 0) break;

            var end = found + ps.FormattedValue.Length;
            if (IsAtTokenBoundary(message, found, end))
                output.Add(new ParameterMatch(found, ps.FormattedValue.Length, ps.SemanticStyle));

            searchIndex = end;
        }
    }

    private static bool IsAtTokenBoundary(string message, int start, int end)
    {
        if (start > 0 && !RenderingChars.IsTokenSeparator(message[start - 1]))
            return false;
        return end >= message.Length || RenderingChars.IsTokenSeparator(message[end]);
    }

    private void AppendNonUrlTokenAwareSegments(
        RenderContext context,
        string message,
        int startOffset,
        int length,
        RenderTokenWriter writer)
    {
        if (length <= 0)
            return;

        if (!tokenResolution.HasEnrichers)
        {
            writer.Add(message, startOffset, length, SemanticStyle.Text);
            return;
        }

        var endOffset = startOffset + length;
        var cursor = startOffset;
        while (cursor < endOffset)
        {
            var separatorIndex = FindNextSeparator(message, cursor, endOffset);
            if (separatorIndex < 0)
            {
                AppendTokenCandidate(context, message, cursor, endOffset - cursor, writer);
                break;
            }

            if (separatorIndex > cursor)
                AppendTokenCandidate(context, message, cursor, separatorIndex - cursor, writer);

            writer.Add(message, separatorIndex, 1, SemanticStyle.Punctuation);
            cursor = separatorIndex + 1;
        }
    }

    private void AppendTokenCandidate(
        RenderContext context,
        string message,
        int start,
        int length,
        RenderTokenWriter writer,
        SemanticStyle fallbackSemanticStyle = SemanticStyle.Text)
    {
        if (length <= 0)
            return;

        if (tokenResolution.TryResolveToken(context, message, start, length, writer))
            return;

        writer.Add(message, start, length, fallbackSemanticStyle);
    }

    private static int FindNextSeparator(string message, int start, int endExclusive)
    {
#if NET8_0_OR_GREATER
        var idx = message.AsSpan(start, endExclusive - start).IndexOfAny(TokenSeparators);
        return idx >= 0 ? start + idx : -1;
#else
        for (var i = start; i < endExclusive; i++)
        {
            if (RenderingChars.IsTokenSeparator(message[i]))
                return i;
        }

        return -1;
#endif
    }

    private readonly record struct ParameterMatch(int Start, int Length, SemanticStyle Style);
}
