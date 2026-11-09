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
internal sealed class ScintillaSearchService(ScintillaNET.Scintilla scintilla)
{
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

    // Find in selection: the range searches are limited to, in live byte positions.
    private bool _hasScope;
    private int _scopeStart;
    private int _scopeEnd;

    // The match FindNext or FindPrevious moved to, in live byte positions; -1 when there is none.
    private int _currentStart = -1;
    private int _currentEnd = -1;

    public bool HasActiveSearch { get; private set; }

    /// <summary>Raised when the document is replaced, which also ends a find in selection.</summary>
    public event Action? ScopeLost;

    public SearchMatch FindNext(string pattern, SearchOptions options)
    {
        SetActiveSearch(pattern, options);
        var (lo, hi) = SearchBounds();
        if (!HasActiveSearch || hi <= lo)
            return SearchMatch.NotFound;

        ApplySearchFlags();
        // After the current match while the caret is still on it; from the caret once the user moved it.
        var caret = (int)Send(LogConstants.SciGetCurrentPos);
        var from = Clamp(caret == _currentStart ? _currentEnd : caret, lo, hi);
        var found = FindForward(from, hi);
        if (found.Start < 0 && from > lo)
            found = FindForward(lo, from);

        return FinalizeFind(found);
    }

    public SearchMatch FindPrevious(string pattern, SearchOptions options)
    {
        SetActiveSearch(pattern, options);
        var (lo, hi) = SearchBounds();
        if (!HasActiveSearch || hi <= lo)
            return SearchMatch.NotFound;

        ApplySearchFlags();
        var from = Clamp((int)Send(LogConstants.SciGetCurrentPos), lo, hi);

        // A target that runs backwards makes Scintilla return the last match in the range.
        var found = FindOnce(from, lo);
        if (found.Start < 0 && from < hi)
            found = FindOnce(hi, from);

        return FinalizeFind(found);
    }

    /// <summary>
    /// Moves the caret to the match and marks it, without selecting it: a selection would lay its own color over
    /// the mark, gray while the find bar has the focus, and would replace a range the user selected.
    /// </summary>
    private SearchMatch FinalizeFind((int Start, int End) found)
    {
        ClearCurrentMatch();
        if (found.Start >= 0)
        {
            _currentStart = found.Start;
            _currentEnd = found.End;
            Send(LogConstants.SciGotoPos, found.Start);
            Send(LogConstants.SciScrollRange, found.End, found.Start);
            Send(LogConstants.SciSetIndicatorCurrent, LogConstants.CurrentMatchIndicatorId);
            Send(LogConstants.SciIndicatorFillRange, found.Start, found.End - found.Start);
        }

        HighlightVisibleMatches();
        return found.Start < 0
            ? SearchMatch.NotFound
            : new SearchMatch(true, RankOf(found.Start));
    }

    public void HighlightSearch(string pattern, SearchOptions options)
    {
        SetActiveSearch(pattern, options);
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
        var (lo, hi) = SearchBounds();
        if (!HasActiveSearch || _patternInvalid || hi <= lo)
        {
            count = LiveMatchCount;
            return true;
        }

        ApplySearchFlags();
        var deadline = Stopwatch.GetTimestamp() + budgetMs * Stopwatch.Frequency / 1000;
        var complete = ScanInSlices(lo, hi, deadline);
        count = LiveMatchCount;
        return complete;
    }

    /// <summary>Scans a few lines at a time until the end or the deadline. Returns false if the deadline came first.</summary>
    private bool ScanInSlices(int lo, int hi, long deadline)
    {
        _scannedTo = Math.Max(_scannedTo, lo);
        while (_scannedTo < hi)
        {
            var line = (int)Send(LogConstants.SciLineFromPosition, _scannedTo);
            var sliceEnd = Math.Min(hi, (int)Send(LogConstants.SciGetLineEndPosition, line + LogConstants.CountSliceLines));
            ScanTo(sliceEnd, deadline);
            if (_patternInvalid)
                return true;
            if (Stopwatch.GetTimestamp() >= deadline && _scannedTo < hi)
                return false;

            // The last line of the document has nothing after its line end.
            if (sliceEnd >= hi)
                return true;
        }

        return true;
    }

    /// <summary>
    /// Limits every search to <paramref name="start"/>..<paramref name="end"/>, or lifts the limit when
    /// <paramref name="enabled"/> is false. The range is marked with its own indicator.
    /// </summary>
    public void SetScope(bool enabled, int start = 0, int end = 0)
    {
        ClearScopeIndicator();
        _hasScope = enabled;
        _scopeStart = enabled ? Math.Max(0, start) : 0;
        _scopeEnd = enabled ? Math.Max(_scopeStart, end) : 0;
        ResetMatchIndex();

        if (enabled && _scopeEnd > _scopeStart)
        {
            Send(LogConstants.SciSetIndicatorCurrent, LogConstants.SearchScopeIndicatorId);
            Send(LogConstants.SciIndicatorFillRange, _scopeStart, _scopeEnd - _scopeStart);
        }

        RefreshHighlightIfActive();
    }

    /// <summary>
    /// The 1-based rank of the current match, or 0 when there is none or the user moved the caret off it. Lets a
    /// count that changes as logs arrive and leave keep the position it shows next to it right.
    /// </summary>
    public int RankOfCurrentMatch()
    {
        if (!HasActiveSearch || _patternInvalid || _currentStart < 0)
            return 0;
        if (Send(LogConstants.SciGetCurrentPos) != _currentStart)
            return 0;

        var (lo, hi) = SearchBounds();
        if (_currentStart < lo || _currentEnd > hi)
            return 0;

        var rank = RankOf(_currentStart);
        var index = _firstLiveMatch + rank - 1;
        return index < _matchIndex.Count && _matchIndex[index] == _currentStart + _trimmedBytes ? rank : 0;
    }

    /// <summary>The start of the document was deleted: every position moved down by this many bytes.</summary>
    public void OnHeadTrimmed(int bytes)
    {
        if (_hasScope)
        {
            _scopeStart = Math.Max(0, _scopeStart - bytes);
            _scopeEnd = Math.Max(0, _scopeEnd - bytes);
        }

        if (_currentStart >= bytes)
        {
            _currentStart -= bytes;
            _currentEnd -= bytes;
        }
        else
        {
            _currentStart = _currentEnd = -1;
        }

        _trimmedBytes += bytes;
        _scannedTo = Math.Max(0, _scannedTo - bytes);

        var index = _matchIndex.BinarySearch(_firstLiveMatch, _matchIndex.Count - _firstLiveMatch, _trimmedBytes, null);
        _firstLiveMatch = index >= 0 ? index : ~index;

        if (_firstLiveMatch <= _matchIndex.Count / 2)
            return;

        _matchIndex.RemoveRange(0, _firstLiveMatch);
        _firstLiveMatch = 0;
    }

    /// <summary>The whole document was replaced or emptied.</summary>
    public void OnDocumentReset()
    {
        ResetMatchIndex();
        if (!_hasScope)
            return;

        // The range pointed into text that is gone.
        _hasScope = false;
        _scopeStart = 0;
        _scopeEnd = 0;
        ScopeLost?.Invoke();
    }

    private int LiveMatchCount => _matchIndex.Count - _firstLiveMatch;

    private int DocumentLength => (int)Send(LogConstants.SciGetLength);

    /// <summary>The range searches cover: the whole document, or the find-in-selection range.</summary>
    private (int Lo, int Hi) SearchBounds()
    {
        var length = DocumentLength;
        return _hasScope
            ? (Math.Min(_scopeStart, length), Math.Min(_scopeEnd, length))
            : (0, length);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
            return min;
        return value > max ? max : value;
    }

    /// <summary>Returns the 1-based rank of the match starting at <paramref name="position"/>.</summary>
    private int RankOf(int position)
    {
        if (position >= _scannedTo)
        {
            var (lo, hi) = SearchBounds();
            ApplySearchFlags();
            _scannedTo = Math.Max(_scannedTo, lo);
            var lineEnd = (int)Send(LogConstants.SciGetLineEndPosition, Send(LogConstants.SciLineFromPosition, position));
            ScanTo(Math.Min(lineEnd, hi), long.MaxValue);
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
            position = (int)scintilla.DirectMessage(LogConstants.SciSearchInTarget, (IntPtr)_patternBytes.Length, (IntPtr)pattern);

        if (position != -2)
        {
            return position < 0
                ? (-1, -1)
                : (position, (int)Send(LogConstants.SciGetTargetEnd));
        }

        // An invalid expression. Scintilla reports it through its status too, and nothing clears that by itself.
        Send(LogConstants.SciSetStatus);
        _patternInvalid = true;
        return (-1, -1);
    }

    private void HighlightVisibleMatches()
    {
        ClearAllHighlight();
        if (!HasActiveSearch || DocumentLength == 0)
            return;

        var (start, end) = GetVisibleRange();
        var (lo, hi) = SearchBounds();
        start = Math.Max(start, lo);
        end = Math.Min(end, hi);
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

    private void ClearCurrentMatch()
    {
        _currentStart = _currentEnd = -1;
        Send(LogConstants.SciSetIndicatorCurrent, LogConstants.CurrentMatchIndicatorId);
        Send(LogConstants.SciIndicatorClearRange, 0, DocumentLength);
        Send(LogConstants.SciSetIndicatorCurrent, LogConstants.SearchIndicatorId);
    }

    private void ClearScopeIndicator()
    {
        Send(LogConstants.SciSetIndicatorCurrent, LogConstants.SearchScopeIndicatorId);
        Send(LogConstants.SciIndicatorClearRange, 0, DocumentLength);
        Send(LogConstants.SciSetIndicatorCurrent, LogConstants.SearchIndicatorId);
    }

    public void ApplyTheme(IScintillaTheme theme)
    {
        ConfigureSearchIndicator(theme);
        ConfigureScopeIndicator(theme);
        ConfigureCurrentMatchIndicator(theme);
    }

    private void ConfigureCurrentMatchIndicator(IScintillaTheme theme)
    {
        var indicator = scintilla.Indicators[LogConstants.CurrentMatchIndicatorId];
        indicator.Style = IndicatorStyle.RoundBox;
        indicator.ForeColor = theme.IsDark ? LogConstants.DarkCurrentMatchColor : LogConstants.LightCurrentMatchColor;
        indicator.Under = true;
        indicator.Alpha = theme.IsDark ? LogConstants.DarkCurrentMatchAlpha : LogConstants.LightCurrentMatchAlpha;
        indicator.OutlineAlpha = 255;
    }

    private void ConfigureScopeIndicator(IScintillaTheme theme)
    {
        var indicator = scintilla.Indicators[LogConstants.SearchScopeIndicatorId];
        indicator.Style = IndicatorStyle.FullBox;
        indicator.ForeColor = theme.Foreground;
        indicator.Under = true;
        indicator.Alpha = theme.IsDark ? LogConstants.DarkScopeIndicatorAlpha : LogConstants.LightScopeIndicatorAlpha;
        indicator.OutlineAlpha = 0;
    }

    private void ConfigureSearchIndicator(IScintillaTheme theme)
    {
        var indicator = scintilla.Indicators[LogConstants.SearchIndicatorId];
        indicator.Style = IndicatorStyle.RoundBox;
        indicator.ForeColor = theme.SearchHighlight;
        indicator.Under = true;
        indicator.Alpha = theme.IsDark ? LogConstants.DarkSearchIndicatorAlpha : LogConstants.LightSearchIndicatorAlpha;
        indicator.OutlineAlpha = theme.IsDark ? LogConstants.DarkSearchIndicatorOutlineAlpha : LogConstants.LightSearchIndicatorOutlineAlpha;
    }

    private void ApplySearchFlags() => Send(LogConstants.SciSetSearchFlags, _activeFlags);

    private static int BuildSearchFlags(SearchOptions options)
    {
        var flags = SearchFlags.None;
        if ((options & SearchOptions.MatchCase) != 0)
            flags |= SearchFlags.MatchCase;
        if ((options & SearchOptions.Regex) != 0)
            flags |= SearchFlags.Regex | SearchFlags.Cxx11Regex;
        // Scintilla ignores whole word for regular expressions, which get word boundaries in the pattern instead.
        else if ((options & SearchOptions.WholeWord) != 0)
            flags |= SearchFlags.WholeWord;

        return (int)flags;
    }

    /// <summary>
    /// Makes <paramref name="pattern"/> the pattern searches use. A count calls it too, because it must search for
    /// its pattern even if nothing has been highlighted yet. An unchanged pattern keeps the matches found so far.
    /// </summary>
    public void SetActiveSearch(string pattern, SearchOptions options)
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

        var flags = BuildSearchFlags(options);
        if (options.HasFlag(SearchOptions.Regex | SearchOptions.WholeWord))
            pattern = $@"\b(?:{pattern})\b";

        if (pattern != _activePattern || flags != _activeFlags)
        {
            _patternBytes = Encoding.UTF8.GetBytes(pattern);
            ResetMatchIndex();
        }

        _activePattern = pattern;
        _activeFlags = flags;
        HasActiveSearch = true;
    }

    private void ResetMatchIndex()
    {
        ClearCurrentMatch();
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
        (long)scintilla.DirectMessage(message, new IntPtr(wParam), new IntPtr(lParam));
}
