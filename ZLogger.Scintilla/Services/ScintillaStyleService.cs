using System;
using System.Buffers;
using System.Collections.Generic;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
// ReSharper disable RedundantCast
namespace ZLogger.Scintilla.Services;

internal sealed class ScintillaStyleService(
    ScintillaNET.Scintilla scintilla, 
    ScintillaOptions options, 
    List<TokenRange> tokenRanges, 
    Dictionary<int, int> linkStyleByBaseStyleId)
{
    private byte[] _styleIdBuffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
    private readonly Dictionary<int, int> _colorizerStyleByArgb = new();
    private int _nextColorizerStyleId = LogConstants.ColorizerStyleIdStart;

    /// <summary>
    /// Applies pre-prepared style and token ranges from a <see cref="PreparedBatch"/>.
    /// Token ranges use relative offsets (0-based); <paramref name="byteStartPos"/> is added.
    /// </summary>
    public void ApplyPreparedStyles(
        int byteStartPos, int writtenLength,
        StyleRange[] styleRanges, int styleRangeCount,
        TokenRange[] tokenRangesArray, int tokenRangeCount)
    {
        if (writtenLength <= 0)
            return;

        EnsureStyleIdBuffer(writtenLength);

        var cursor = 0;
        for (var i = 0; i < styleRangeCount; i++)
        {
            var range = styleRanges[i];
            var styleId = ResolveStyleId(range);
            var clampedId = (byte)(styleId & 0xFF);
            _styleIdBuffer.AsSpan(cursor, range.Length).Fill(clampedId);
            cursor += range.Length;
        }

        ApplyStyleBuffer(byteStartPos, writtenLength);

        scintilla.DirectMessage(LogConstants.SciIndicatorClearRange, (IntPtr)byteStartPos, (IntPtr)writtenLength);
        scintilla.IndicatorCurrent = StyleMapper.TokenIndicatorStyleId;

        for (var i = 0; i < tokenRangeCount; i++)
        {
            var tr = tokenRangesArray[i];
            if (options.EnableTokenLinks && tr.Payload is { } tp)
                tokenRanges.Add(new TokenRange(byteStartPos + tr.Start, tr.Length, tp));
        }
    }

    private unsafe void ApplyStyleBuffer(int byteStartPos, int writtenLength)
    {
        scintilla.DirectMessage(LogConstants.SciStartStyling, (IntPtr)byteStartPos);
        fixed (byte* ptr = _styleIdBuffer)
        {
            scintilla.DirectMessage(LogConstants.SciSetStylingEx, (IntPtr)writtenLength, (IntPtr)ptr);
        }
    }

    public void RefreshLinkStylesFromBaseStyles()
    {
        foreach (var pair in linkStyleByBaseStyleId)
            CopyBaseStyleToLinkStyle(pair.Key, pair.Value);
    }

    internal void ReleaseBuffer()
    {
        if (_styleIdBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_styleIdBuffer);
            _styleIdBuffer = [];
        }
    }

    private void EnsureStyleIdBuffer(int requiredSize)
    {
        if (requiredSize <= _styleIdBuffer.Length)
            return;

        ArrayPool<byte>.Shared.Return(_styleIdBuffer);
        _styleIdBuffer = ArrayPool<byte>.Shared.Rent(requiredSize);
    }

    private int ResolveStyleId(StyleRange range)
    {
        if (range.IsLink)
            return ResolveLinkStyleId(range.StyleId);
        if (range.TokenPayload is { HasColorizerColor: true } colorPayload)
            return ResolveColorizerStyleId(colorPayload.ColorizerForeground!.Value, range.StyleId);
        return range.StyleId;
    }

    private int ResolveLinkStyleId(int baseStyleId)
    {
        if (linkStyleByBaseStyleId.TryGetValue(baseStyleId, out var resolved))
            return resolved;

        var candidateStyleId = baseStyleId + LogConstants.LinkStyleOffset;
        if (candidateStyleId is <= 0 or >= 255)
            candidateStyleId = StyleMapper.LinkHotspotStyleId;

        if (candidateStyleId != StyleMapper.LinkHotspotStyleId)
            CopyBaseStyleToLinkStyle(baseStyleId, candidateStyleId);

        linkStyleByBaseStyleId[baseStyleId] = candidateStyleId;
        return candidateStyleId;
    }

    private void CopyBaseStyleToLinkStyle(int baseStyleId, int linkStyleId)
    {
        var source = scintilla.Styles[baseStyleId];
        var target = scintilla.Styles[linkStyleId];
        target.ForeColor = source.ForeColor;
        target.BackColor = source.BackColor;
        target.Bold = source.Bold;
        target.Hotspot = true;
        target.Underline = true;
    }

    private int ResolveColorizerStyleId(System.Drawing.Color foreground, int baseStyleId)
    {
        var argb = foreground.ToArgb();
        if (_colorizerStyleByArgb.TryGetValue(argb, out var existing))
            return existing;

        if (_nextColorizerStyleId > LogConstants.ColorizerStyleIdEnd)
            return baseStyleId;

        var newStyleId = _nextColorizerStyleId++;
        _colorizerStyleByArgb[argb] = newStyleId;

        var baseStyle = scintilla.Styles[baseStyleId];
        var target = scintilla.Styles[newStyleId];
        target.ForeColor = foreground;
        target.BackColor = baseStyle.BackColor;
        target.Bold = true;

        return newStyleId;
    }

    internal void ResetColorizerStyles()
    {
        _colorizerStyleByArgb.Clear();
        _nextColorizerStyleId = LogConstants.ColorizerStyleIdStart;
    }
}