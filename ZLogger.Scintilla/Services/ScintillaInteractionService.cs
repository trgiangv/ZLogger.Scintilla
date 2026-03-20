using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using CommunityToolkit.HighPerformance.Buffers;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
// ReSharper disable RedundantCast
namespace ZLogger.Scintilla.Services;

internal sealed class ScintillaInteractionService(ScintillaNET.Scintilla scintilla, ScintillaOptions options, List<TokenRange> tokenRanges)
{
    private bool _isHoveringLink;

    public void HandleDoubleClick()
    {
        if (!options.EnableTokenLinks)
            return;

        var position = (int)scintilla.DirectMessage(LogConstants.SciGetCurrentPos, IntPtr.Zero, IntPtr.Zero);
        if (TryGetTokenRangeAt(position, out var mapped))
        {
            InvokeTokenClick(mapped.Payload);
            return;
        }

        // Fallback: try to match selected text against linkifiers
        var selected = scintilla.SelectedText;
        if (string.IsNullOrWhiteSpace(selected))
            return;

        var maxBytes = Encoding.UTF8.GetMaxByteCount(selected.Length);
        using var owner = SpanOwner<byte>.Allocate(maxBytes);
#if NET6_0_OR_GREATER
        var written = Encoding.UTF8.GetBytes(selected.AsSpan(), owner.Span);
#else
        var written = Encoding.UTF8.GetBytes(selected, 0, selected.Length, owner.DangerousGetArray().Array!, owner.DangerousGetArray().Offset);
#endif
        var utf8 = owner.Span[..written];
        if (options.Linkifier is null) return;
        if (!options.Linkifier.TryMatch(utf8, RenderContext.Empty, out var onClick) || onClick is null) return;
        InvokeAction(onClick);
    }

    public void HandleMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !options.EnableTokenLinks)
            return;
        var position = (int)scintilla.DirectMessage(LogConstants.SciPositionFromPointClose, (IntPtr)e.X, (IntPtr)e.Y);
        if (position < 0 || !TryGetTokenRangeAt(position, out var tokenRange))
            return;

        InvokeTokenClick(tokenRange.Payload);
    }

    public void HandleMouseMove(MouseEventArgs e)
    {
        if (!options.EnableTokenLinks)
            return;
        var position = (int)scintilla.DirectMessage(LogConstants.SciPositionFromPointClose, (IntPtr)e.X, (IntPtr)e.Y);
        var isOnLink = position >= 0 && TryGetTokenRangeAt(position, out _);
        if (isOnLink == _isHoveringLink)
            return;

        _isHoveringLink = isOnLink;
        scintilla.Cursor = isOnLink ? Cursors.Hand : Cursors.IBeam;
    }

    public void HandleMouseLeave()
    {
        if (!_isHoveringLink)
            return;

        _isHoveringLink = false;
        scintilla.Cursor = Cursors.IBeam;
    }

    public void TrimTokenRangesIfNeeded(int maxCount)
    {
        if (tokenRanges.Count <= maxCount)
            return;

        var excess = tokenRanges.Count - maxCount;
        tokenRanges.RemoveRange(0, excess);
    }

    private bool TryGetTokenRangeAt(int position, out TokenRange tokenRange)
    {
        var lo = 0;
        var hi = tokenRanges.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var mapped = tokenRanges[mid];
            if (position < mapped.Start)
            {
                hi = mid - 1;
                continue;
            }

            var end = mapped.Start + mapped.Length;
            if (position >= end)
            {
                lo = mid + 1;
                continue;
            }

            tokenRange = mapped;
            return true;
        }

        tokenRange = default;
        return false;
    }

    private void InvokeTokenClick(TokenPayload payload)
    {
        if (payload.IsLink)
            InvokeAction(payload.InvokeClick);
    }

    private void InvokeAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            options.EnrichmentErrorSink?.Invoke(ex);
        }
    }
}
