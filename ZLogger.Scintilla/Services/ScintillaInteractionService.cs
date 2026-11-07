using System.Windows.Forms;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
// ReSharper disable RedundantCast
namespace ZLogger.Scintilla.Services;

internal sealed class ScintillaInteractionService(LogScintillaControl scintilla, ScintillaOptions options, List<TokenRange> tokenRanges)
{
    private bool _isHoveringLink;

    /// <summary>
    /// A link opens on a plain single click. The up of a double click would open it a second time,
    /// and a drag that selects text and happens to end on a link is not a click at all.
    /// </summary>
    public void HandleMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || scintilla.IsInDoubleClick)
            return;
        if (scintilla.DirectMessage(LogConstants.SciGetSelectionEmpty, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero)
            return;

        var position = (int)scintilla.DirectMessage(LogConstants.SciPositionFromPointClose, (IntPtr)e.X, (IntPtr)e.Y);
        if (position < 0 || !TryGetTokenRangeAt(position, out var tokenRange))
            return;

        try
        {
            tokenRange.Payload.InvokeClick();
        }
        catch (Exception ex)
        {
            options.EnrichmentErrorSink?.Invoke(ex);
        }
    }

    public void HandleMouseMove(MouseEventArgs e)
    {
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
}
