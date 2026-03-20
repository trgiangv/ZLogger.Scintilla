using System;
using System.Collections.Generic;
using ScintillaNET;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
// ReSharper disable RedundantSuppressNullableWarningExpression
namespace ZLogger.Scintilla.Services;

internal sealed class ScintillaSearchService
{
    private readonly ScintillaNET.Scintilla _scintilla;
    private string? _activePattern;
    private SearchFlags _activeFlags;
    private bool _hasActiveSearch;

    private int[]? _cachedMatchPositions;
    private string? _cachePattern;
    private SearchFlags _cacheFlags;

    public ScintillaSearchService(ScintillaNET.Scintilla scintilla)
    {
        _scintilla = scintilla;
        ConfigureSearchIndicator(ScintillaThemes.Dark);
    }

    public SearchMatch FindNext(string pattern, bool matchCase, bool useRegex)
    {
        SetActiveSearch(pattern, matchCase, useRegex);
        if (string.IsNullOrWhiteSpace(pattern) || _scintilla.TextLength == 0)
            return SearchMatch.NotFound;

        var flags = BuildSearchFlags(matchCase, useRegex);
        var start = Math.Clamp(_scintilla.CurrentPosition, 0, _scintilla.TextLength);
        var found = SearchWithWrap(pattern, flags, start, _scintilla.TextLength);

        if (found.Found)
            _scintilla.SetSelection(found.Position + found.Length, found.Position);

        return FinalizeFind(pattern, flags, found);
    }

    public SearchMatch FindPrevious(string pattern, bool matchCase, bool useRegex)
    {
        SetActiveSearch(pattern, matchCase, useRegex);
        if (string.IsNullOrWhiteSpace(pattern) || _scintilla.TextLength == 0)
            return SearchMatch.NotFound;

        var flags = BuildSearchFlags(matchCase, useRegex);
        var start = Math.Clamp(_scintilla.CurrentPosition - 1, 0, _scintilla.TextLength);
        var found = SearchBackwardWithWrap(pattern, flags, start, _scintilla.TextLength);

        if (found.Found)
            _scintilla.SetSelection(found.Position, found.Position + found.Length);

        return FinalizeFind(pattern, flags, found);
    }

    private SearchMatch FinalizeFind(string pattern, SearchFlags flags, (bool Found, int Position, int Length) found)
    {
        HighlightVisibleMatches(pattern, flags);
        if (!found.Found)
            return SearchMatch.NotFound;

        var currentIndex = CountMatchesUpToPosition(pattern, flags, found.Position);
        return new SearchMatch(true, currentIndex);
    }

    /// <summary>
    /// Returns the 1-based rank of the match at <paramref name="matchPosition"/>.
    /// Uses the cached sorted positions array (O(log n)) when available;
    /// falls back to a linear scan (O(k)) otherwise.
    /// </summary>
    private int CountMatchesUpToPosition(string pattern, SearchFlags flags, int matchPosition)
    {
        if (matchPosition < 0 || _scintilla.TextLength == 0)
            return 1;

        var cache = _cachedMatchPositions;
        if (cache is not null && _cachePattern == pattern && _cacheFlags == flags)
            return RankFromCache(cache, matchPosition);

        return LinearScanRankUpTo(pattern, flags, matchPosition);
    }

    private static int RankFromCache(int[] sortedPositions, int matchPosition)
    {
        var idx = Array.BinarySearch(sortedPositions, matchPosition);
        return (idx >= 0 ? idx : ~idx) + 1;
    }

    private int LinearScanRankUpTo(string pattern, SearchFlags flags, int matchPosition)
    {
        _scintilla.SearchFlags = flags;
        var count = 0;
        var scanStart = 0;

        while (scanStart <= matchPosition)
        {
            _scintilla.TargetStart = scanStart;
            _scintilla.TargetEnd = matchPosition + 1;
            var pos = _scintilla.SearchInTarget(pattern);
            if (pos < 0)
                break;

            count++;
            var len = _scintilla.TargetEnd - pos;
            scanStart = pos + Math.Max(len, 1);

            if (pos >= matchPosition)
                break;
        }

        return Math.Max(1, count);
    }

    /// <summary>
    /// Builds (or rebuilds) the sorted match-positions cache for the current pattern.
    /// Must be called on the UI thread. Intended to be called once after
    /// <c>CountMatchesAsync</c> completes so that subsequent FindNext/FindPrevious
    /// calls use O(log n) binary search instead of O(k) linear scan.
    /// </summary>
    public void BuildMatchIndex()
    {
        var pattern = _activePattern;
        if (!_hasActiveSearch || string.IsNullOrWhiteSpace(pattern) || _scintilla.TextLength == 0)
        {
            _cachedMatchPositions = null;
            return;
        }

        var flags = _activeFlags;

        if (_cachePattern == pattern && _cacheFlags == flags && _cachedMatchPositions is not null)
            return;

        _cachedMatchPositions = ScanAllMatchPositions(pattern!, flags);
        _cachePattern = pattern;
        _cacheFlags = flags;
    }

    private int[] ScanAllMatchPositions(string pattern, SearchFlags flags)
    {
        _scintilla.SearchFlags = flags;
        var positions = new List<int>(LogConstants.MatchPositionsCapacity);
        var scanStart = 0;
        var textLength = _scintilla.TextLength;

        while (scanStart < textLength)
        {
            _scintilla.TargetStart = scanStart;
            _scintilla.TargetEnd = textLength;
            var pos = _scintilla.SearchInTarget(pattern);
            if (pos < 0)
                break;

            positions.Add(pos);
            var len = _scintilla.TargetEnd - pos;
            scanStart = pos + Math.Max(len, 1);
        }

        return positions.ToArray();
    }

    /// <summary>
    /// Invalidates the match-positions cache (e.g. when the document is appended or cleared).
    /// </summary>
    public void InvalidateMatchIndex()
    {
        _cachedMatchPositions = null;
    }

    public SearchMatch HighlightSearch(string pattern, bool matchCase, bool useRegex)
    {
        SetActiveSearch(pattern, matchCase, useRegex);
        RefreshHighlightIfActive();
        return SearchMatch.NotFound;
    }

    public bool HasActiveSearch => _hasActiveSearch;

    public void RefreshHighlightIfActive()
    {
        var pattern = _activePattern;
        if (!_hasActiveSearch || string.IsNullOrWhiteSpace(pattern))
        {
            ClearSearchHighlight();
            return;
        }

        HighlightVisibleMatches(pattern!, _activeFlags);
    }

    private void ClearSearchHighlight()
    {
        _activePattern = null;
        _activeFlags = SearchFlags.None;
        _hasActiveSearch = false;
        _cachedMatchPositions = null;
        ClearAllHighlight();
    }

    private (bool Found, int Position, int Length) SearchWithWrap(string pattern, SearchFlags flags, int start, int end)
    {
        _scintilla.SearchFlags = flags;

        var found = SearchInRange(pattern, start, end);
        if (found.Found)
            return found;

        if (start <= 0)
            return (false, -1, 0);

        return SearchInRange(pattern, 0, start);
    }

    private (bool Found, int Position, int Length) SearchInRange(string pattern, int start, int end)
    {
        _scintilla.TargetStart = start;
        _scintilla.TargetEnd = end;

        var foundPosition = _scintilla.SearchInTarget(pattern);
        if (foundPosition < 0)
            return (false, -1, 0);

        var matchLength = _scintilla.TargetEnd - _scintilla.TargetStart;
        return (true, foundPosition, Math.Max(0, matchLength));
    }

    private (bool Found, int Position, int Length) SearchBackwardWithWrap(string pattern, SearchFlags flags, int start, int end)
    {
        _scintilla.SearchFlags = flags;

        // Reversed TargetStart/TargetEnd signals Scintilla to search backward.
        var found = SearchInRange(pattern, start, 0);
        if (found.Found)
            return found;

        if (start >= end)
            return (false, -1, 0);

        return SearchInRange(pattern, end, start + 1);
    }

    private void HighlightVisibleMatches(string pattern, SearchFlags flags)
    {
        ClearAllHighlight();

        if (string.IsNullOrWhiteSpace(pattern) || _scintilla.TextLength == 0)
            return;

        var (startPos, endPos) = GetVisibleRange();
        if (endPos <= startPos)
            return;

        _scintilla.SearchFlags = flags;
        var scanStart = startPos;
        while (scanStart < endPos)
        {
            _scintilla.TargetStart = scanStart;
            _scintilla.TargetEnd = endPos;
            var foundPosition = _scintilla.SearchInTarget(pattern);
            if (foundPosition < 0)
                break;

            var matchStart = _scintilla.TargetStart;
            var matchEnd = _scintilla.TargetEnd;
            var matchLength = matchEnd - matchStart;
            if (matchLength <= 0)
                break;

            _scintilla.IndicatorCurrent = LogConstants.SearchIndicatorId;
            _scintilla.IndicatorFillRange(matchStart, matchLength);
            scanStart = Math.Max(matchEnd, matchStart + 1);
        }
    }

    private void ClearAllHighlight()
    {
        _scintilla.IndicatorCurrent = LogConstants.SearchIndicatorId;
        _scintilla.IndicatorClearRange(0, _scintilla.TextLength);
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

    private static SearchFlags BuildSearchFlags(bool matchCase, bool useRegex)
    {
        var flags = SearchFlags.None;
        if (matchCase)
            flags |= SearchFlags.MatchCase;
        if (useRegex)
            flags |= SearchFlags.Regex;

        return flags;
    }

    private void SetActiveSearch(string pattern, bool matchCase, bool useRegex)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            ClearSearchHighlight();
            return;
        }

        var newFlags = BuildSearchFlags(matchCase, useRegex);

        if (pattern != _activePattern || newFlags != _activeFlags)
            _cachedMatchPositions = null;

        _activePattern = pattern;
        _activeFlags = newFlags;
        _hasActiveSearch = true;
    }

    private (int Start, int End) GetVisibleRange()
    {
        if (_scintilla.TextLength == 0)
            return (0, 0);

        var topPos = PositionFromPoint(0, 0);
        var rect = _scintilla.ClientRectangle;
        var bottomPos = PositionFromPoint(rect.Width - 1, rect.Height - 1);

        var start = Math.Max(0, topPos - LogConstants.VisibleRangeBufferBytes);
        var end = Math.Min(_scintilla.TextLength, bottomPos + LogConstants.VisibleRangeBufferBytes);
        return (start, end);
    }

    private int PositionFromPoint(int x, int y)
    {
        // ReSharper disable RedundantCast
        return _scintilla.DirectMessage(LogConstants.SciPositionFromPoint, (IntPtr)x, (IntPtr)y).ToInt32();
        // ReSharper restore RedundantCast
    }
}
