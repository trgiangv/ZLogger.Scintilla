using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Services;
namespace ZLogger.Scintilla.Pipeline;

internal sealed class SearchCoordinator(
    ScintillaDocument document,
    Action<Action> dispatch)
{
    private string _pattern = string.Empty;
    private SearchOptions _options;
    private int _totalMatches;

    private bool IsEmpty => string.IsNullOrEmpty(_pattern);

    public void HighlightSearch(string pattern, bool useRegex = false) =>
        HighlightSearch(pattern, useRegex ? SearchOptions.Regex : SearchOptions.None);

    public void HighlightSearch(string pattern, SearchOptions options)
    {
        if (!UpdatePattern(pattern, options))
            return;

        dispatch(() => document.HighlightSearch(_pattern, _options));
    }

    public void ClearSearch()
    {
        ResetPattern();
        dispatch(() => document.HighlightSearch(string.Empty, SearchOptions.None));
    }

    public SearchPosition FindNext() => ExecuteFind(forward: true);
    public Task<SearchPosition> FindNextAsync(CancellationToken ct = default) => ExecuteFindAsync(forward: true, ct);

    public SearchPosition FindPrevious() => ExecuteFind(forward: false);
    public Task<SearchPosition> FindPreviousAsync(CancellationToken ct = default) => ExecuteFindAsync(forward: false, ct);

    public async Task<int> CountMatchesAsync(CancellationToken ct = default)
    {
        if (IsEmpty)
        {
            _totalMatches = 0;
            return 0;
        }

        var total = await document.CountMatchesAsync(_pattern, _options, ct)
            .ConfigureAwait(false);
        _totalMatches = total;
        return total;
    }

    /// <summary>
    /// Synchronous find — dispatches to the UI thread. <see cref="SearchPosition.Total"/> reflects
    /// the last known count from <see cref="CountMatchesAsync"/>; call it first for an accurate total.
    /// </summary>
    private SearchPosition ExecuteFind(bool forward)
    {
        if (IsEmpty)
            return SearchPosition.NotFound;

        var holder = new[] { SearchMatch.NotFound };
        dispatch(() => holder[0] = forward
            ? document.FindNext(_pattern, _options)
            : document.FindPrevious(_pattern, _options));

        return !holder[0].Found
            ? SearchPosition.NotFound
            : new SearchPosition { Current = holder[0].CurrentIndex, Total = _totalMatches };
    }

    private async Task<SearchPosition> ExecuteFindAsync(bool forward, CancellationToken ct)
    {
        if (IsEmpty)
            return SearchPosition.NotFound;

        var result = forward
            ? await document.FindNextAsync(_pattern, _options, ct).ConfigureAwait(false)
            : await document.FindPreviousAsync(_pattern, _options, ct).ConfigureAwait(false);

        return !result.Found
            ? SearchPosition.NotFound
            : new SearchPosition { Current = result.CurrentIndex, Total = _totalMatches };
    }

    /// <summary>
    /// Limits searches to the current selection, or lifts the limit. Returns whether a range is now in effect:
    /// nothing is limited when nothing is selected. Must be called on the UI thread.
    /// </summary>
    public bool SetScope(bool enabled)
    {
        _totalMatches = 0;
        return document.SetSearchScope(enabled);
    }

    /// <summary>Where the current match ranks now, with the total from the last count. Must be called on the UI thread.</summary>
    public SearchPosition CurrentPosition(int total)
    {
        _totalMatches = total;
        return IsEmpty
            ? SearchPosition.NotFound
            : new SearchPosition { Current = document.RankOfCurrentMatch(), Total = total };
    }

    private bool UpdatePattern(string? pattern, SearchOptions options)
    {
        var newPattern = pattern ?? string.Empty;
        if (newPattern == _pattern && options == _options)
            return false;

        _pattern = newPattern;
        _options = options;
        _totalMatches = 0;
        return true;
    }

    private void ResetPattern()
    {
        _pattern = string.Empty;
        _options = SearchOptions.None;
        _totalMatches = 0;
    }
}
