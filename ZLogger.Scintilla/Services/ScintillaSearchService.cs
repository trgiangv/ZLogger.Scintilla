using System.Diagnostics;
using System.Text;
using ScintillaNET;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
// ReSharper disable RedundantCast
namespace ZLogger.Scintilla.Services;

/// <summary>
/// Search over the document. Works only in byte positions, which is what Scintilla's own messages use.
/// Regular expressions use the C++11 engine (ECMAScript grammar) for navigation, highlighting and counting
/// alike, so all three always agree. Must be called on the UI thread.
/// </summary>
internal sealed class ScintillaSearchService
{
    private readonly ScintillaNET.Scintilla _scintilla;
    private string? _activePattern;
    private byte[] _patternBytes = [];
    private int _activeFlags;

    // Every match found so far, in document order, and how far the scan has reached. New log lines only add
    // to the end, so this stays valid while logs arrive, and a rank costs one scan of the new text at most.
    // Positions are stored with the bytes trimmed so far added, so trimming does not have to rewrite them.
    private readonly List<long> _matchIndex = new(LogConstants.MatchPositionsCapacity);
    private int _firstLiveMatch;
    private int _scannedTo;
    private long _trimmedBytes;
    private bool _patternInvalid;

    public ScintillaSearchService(ScintillaNET.Scintilla scintilla)
    {
        _scintilla = scintilla;
    }

    public bool HasActiveSearch { get; private set; }

    public SearchMatch FindNext(string pattern, bool matchCase, bool useRegex)
    {
        SetActiveSearch(pattern, matchCase, useRegex);
        var length = DocumentLength;
        if (!HasActiveSearch || length == 0)
            return SearchMatch.NotFound;

        ApplySearchFlags();
        var from = (int)Send(LogConstants.SciGetSelectionEnd);
        var found = FindForward(from, length);
        if (found.Start < 0 && from > 0)
            found = FindForward(0, from);

        if (found.Start >= 0)
            Send(LogConstants.SciSetSel, found.Start, found.End);

        return FinalizeFind(found);
    }

    public SearchMatch FindPrevious(string pattern, bool matchCase, bool useRegex)
    {
        SetActiveSearch(pattern, matchCase, useRegex);
        var length = DocumentLength;
        if (!HasActiveSearch || length == 0)
            return SearchMatch.NotFound;

        ApplySearchFlags();
        var from = (int)Send(LogConstants.SciGetSelectionStart);

        // A target that runs backwards makes Scintilla return the last match in the range.
        var found = FindOnce(from, 0);
        if (found.Start < 0 && from < length)
            found = FindOnce(length, from);

        if (found.Start >= 0)
            Send(LogConstants.SciSetSel, found.End, found.Start);

        return FinalizeFind(found);
    }

    private SearchMatch FinalizeFind((int Start, int End) found)
    {
        HighlightVisibleMatches();
        return found.Start < 0
            ? SearchMatch.NotFound
            : new SearchMatch(true, RankOf(found.Start));
    }

    public void HighlightSearch(string pattern, bool matchCase, bool useRegex)
    {
        SetActiveSearch(pattern, matchCase, useRegex);
        RefreshHighlightIfActive();
    }

    public void RefreshHighlightIfActive()
    {
        if (HasActiveSearch)
            HighlightVisibleMatches();
        else
            ClearAllHighlight();
    }

    /// <summary>Scans a bounded part of the document. Returns true with the match count when the scan is complete.</summary>
    public bool CountSlice(int budgetMs, out int count)
    {
        var length = DocumentLength;
        if (!HasActiveSearch || _patternInvalid || length == 0)
        {
            count = LiveMatchCount;
            return true;
        }

        ApplySearchFlags();
        var deadline = Stopwatch.GetTimestamp() + budgetMs * Stopwatch.Frequency / 1000;
        var complete = ScanInSlices(length, deadline);
        count = LiveMatchCount;
        return complete;
    }

    /// <summary>Scans a few lines at a time until the end or the deadline. Returns false if the deadline came first.</summary>
    private bool ScanInSlices(int length, long deadline)
    {
        while (_scannedTo < length)
        {
            var line = (int)Send(LogConstants.SciLineFromPosition, _scannedTo);
            var sliceEnd = (int)Send(LogConstants.SciGetLineEndPosition, line + LogConstants.CountSliceLines);
            ScanTo(sliceEnd, deadline);
            if (_patternInvalid)
                return true;
            if (Stopwatch.GetTimestamp() >= deadline && _scannedTo < length)
                return false;

            // The last line of the document has nothing after its line end.
            if (sliceEnd >= length)
                return true;
        }

        return true;
    }

    /// <summary>The start of the document was deleted: every position moved down by this many bytes.</summary>
    public void OnHeadTrimmed(int bytes)
    {
        _trimmedBytes += bytes;
        _scannedTo = Math.Max(0, _scannedTo - bytes);

        var index = _matchIndex.BinarySearch(_firstLiveMatch, _matchIndex.Count - _firstLiveMatch, _trimmedBytes, null);
        _firstLiveMatch = index >= 0 ? index : ~index;

        if (_firstLiveMatch > _matchIndex.Count / 2)
        {
            _matchIndex.RemoveRange(0, _firstLiveMatch);
            _firstLiveMatch = 0;
        }
    }

    /// <summary>The whole document was replaced or emptied.</summary>
    public void OnDocumentReset() => ResetMatchIndex();

    private int LiveMatchCount => _matchIndex.Count - _firstLiveMatch;

    private int DocumentLength => (int)Send(LogConstants.SciGetLength);

    /// <summary>Returns the 1-based rank of the match starting at <paramref name="position"/>.</summary>
    private int RankOf(int position)
    {
        if (position >= _scannedTo)
        {
            ApplySearchFlags();
            ScanTo((int)Send(LogConstants.SciGetLineEndPosition, Send(LogConstants.SciLineFromPosition, position)), long.MaxValue);
        }

        var target = position + _trimmedBytes;
        var index = _matchIndex.BinarySearch(_firstLiveMatch, LiveMatchCount, target, null);
        return (index >= 0 ? index : ~index) - _firstLiveMatch + 1;
    }

    private void ScanTo(int end, long deadline)
    {
        var checks = 0;
        while (_scannedTo < end)
        {
            var (start, matchEnd) = FindOnce(_scannedTo, end);
            if (start < 0)
            {
                _scannedTo = end;
                return;
            }

            if (matchEnd > start)
                _matchIndex.Add(start + _trimmedBytes);

            var next = NextSearchStart(start, matchEnd);
            if (next <= _scannedTo)
            {
                _scannedTo = end;
                return;
            }

            _scannedTo = next;
            if ((++checks & 63) == 0 && Stopwatch.GetTimestamp() >= deadline)
                return;
        }
    }

    private int NextSearchStart(int start, int end) =>
        end > start ? end : (int)Send(LogConstants.SciPositionAfter, start);

    /// <summary>The first match with text in it. An expression like <c>x*</c> also matches nothing at every position.</summary>
    private (int Start, int End) FindForward(int from, int to)
    {
        var position = from;
        while (position < to)
        {
            var found = FindOnce(position, to);
            if (found.Start < 0 || found.End > found.Start)
                return found;

            var next = NextSearchStart(found.Start, found.End);
            if (next <= position)
                break;
            position = next;
        }

        return (-1, -1);
    }

    private unsafe (int Start, int End) FindOnce(int start, int end)
    {
        Send(LogConstants.SciSetTargetRange, start, end);
        int position;
        fixed (byte* pattern = _patternBytes)
            position = (int)_scintilla.DirectMessage(LogConstants.SciSearchInTarget, (IntPtr)_patternBytes.Length, (IntPtr)pattern);

        if (position == -2)
        {
            // Scintilla reports an invalid expression through its status too, and nothing clears that by itself.
            Send(LogConstants.SciSetStatus);
            _patternInvalid = true;
        }

        return position < 0
            ? (-1, -1)
            : (position, (int)Send(LogConstants.SciGetTargetEnd));
    }

    private void HighlightVisibleMatches()
    {
        ClearAllHighlight();
        if (!HasActiveSearch || DocumentLength == 0)
            return;

        var (start, end) = GetVisibleRange();
        if (end <= start)
            return;

        ApplySearchFlags();
        FillMatchIndicators(start, end);
    }

    private void FillMatchIndicators(int start, int end)
    {
        var position = start;
        while (position < end)
        {
            var (matchStart, matchEnd) = FindOnce(position, end);
            if (matchStart < 0)
                break;

            if (matchEnd > matchStart)
                Send(LogConstants.SciIndicatorFillRange, matchStart, matchEnd - matchStart);

            // Always moves forward. A search that returned a position behind the cursor would otherwise spin here.
            var next = NextSearchStart(matchStart, matchEnd);
            if (next <= position)
                break;
            position = next;
        }
    }

    private void ClearAllHighlight()
    {
        Send(LogConstants.SciSetIndicatorCurrent, LogConstants.SearchIndicatorId);
        Send(LogConstants.SciIndicatorClearRange, 0, DocumentLength);
    }

    public void ApplyTheme(IScintillaTheme theme)
    {
        ConfigureSearchIndicator(theme);
    }

    private void ConfigureSearchIndicator(IScintillaTheme theme)
    {
        var indicator = _scintilla.Indicators[LogConstants.SearchIndicatorId];
        indicator.Style = IndicatorStyle.RoundBox;
        indicator.ForeColor = theme.SearchHighlight;
        indicator.Under = true;
        indicator.Alpha = theme.IsDark ? LogConstants.DarkSearchIndicatorAlpha : LogConstants.LightSearchIndicatorAlpha;
        indicator.OutlineAlpha = theme.IsDark ? LogConstants.DarkSearchIndicatorOutlineAlpha : LogConstants.LightSearchIndicatorOutlineAlpha;
    }

    private void ApplySearchFlags() => Send(LogConstants.SciSetSearchFlags, _activeFlags);

    private static int BuildSearchFlags(bool matchCase, bool useRegex)
    {
        var flags = SearchFlags.None;
        if (matchCase)
            flags |= SearchFlags.MatchCase;
        if (useRegex)
            flags |= SearchFlags.Regex | SearchFlags.Cxx11Regex;

        return (int)flags;
    }

    private void SetActiveSearch(string pattern, bool matchCase, bool useRegex)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            _activePattern = null;
            _patternBytes = [];
            _activeFlags = 0;
            HasActiveSearch = false;
            ResetMatchIndex();
            ClearAllHighlight();
            return;
        }

        var flags = BuildSearchFlags(matchCase, useRegex);
        if (pattern != _activePattern || flags != _activeFlags)
        {
            _patternBytes = Encoding.UTF8.GetBytes(pattern);
            ResetMatchIndex();
        }

        _activePattern = pattern;
        _activeFlags = flags;
        HasActiveSearch = true;
    }

    /// <summary>Called by a count that must search for this pattern even if nothing has been highlighted yet.</summary>
    public void EnsureActive(string pattern, bool matchCase, bool useRegex) => SetActiveSearch(pattern, matchCase, useRegex);

    private void ResetMatchIndex()
    {
        _matchIndex.Clear();
        _firstLiveMatch = 0;
        _scannedTo = 0;
        _trimmedBytes = 0;
        _patternInvalid = false;
    }

    /// <summary>The visible lines, found through line numbers so that margins and wrapped rows cannot skew them.</summary>
    private (int Start, int End) GetVisibleRange()
    {
        var top = (int)Send(LogConstants.SciGetFirstVisibleLine);
        var rows = (int)Send(LogConstants.SciLinesOnScreen);
        var firstLine = (int)Send(LogConstants.SciDocLineFromVisible, top);
        var lastLine = (int)Send(LogConstants.SciDocLineFromVisible, top + rows);
        return ((int)Send(LogConstants.SciPositionFromLine, firstLine),
                (int)Send(LogConstants.SciGetLineEndPosition, lastLine));
    }

    private long Send(int message, long wParam = 0, long lParam = 0) =>
        (long)_scintilla.DirectMessage(message, (IntPtr)wParam, (IntPtr)lParam);
}
