using System;
using System.Threading;
using System.Threading.Tasks;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Services;
namespace ZLogger.Scintilla.Pipeline;

internal sealed class SearchCoordinator(
    ScintillaDocument document,
    Action<Action> dispatch,
    Func<Func<SearchMatch>, CancellationToken, Task<SearchMatch>> dispatchAsync)
{
    private string _pattern = string.Empty;
    private bool _useRegex;
    private int _totalMatches;

    private bool IsEmpty => string.IsNullOrEmpty(_pattern);

    public void HighlightSearch(string pattern, bool useRegex = false)
    {
        if (!UpdatePattern(pattern, useRegex))
            return;

        if (IsEmpty)
        {
            dispatch(() => document.HighlightSearch(string.Empty, matchCase: false, useRegex: false));
            return;
        }

        dispatch(() => document.HighlightSearch(_pattern, matchCase: false, _useRegex));
    }

    public void ClearSearch()
    {
        ResetPattern();
        dispatch(() => document.HighlightSearch(string.Empty, matchCase: false, useRegex: false));
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

        var total = await document.CountMatchesAsync(_pattern, matchCase: false, _useRegex, ct)
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
            ? document.FindNext(_pattern, matchCase: false, _useRegex)
            : document.FindPrevious(_pattern, matchCase: false, _useRegex));

        return !holder[0].Found
            ? SearchPosition.NotFound
            : new SearchPosition { Current = holder[0].CurrentIndex, Total = _totalMatches };
    }

    private async Task<SearchPosition> ExecuteFindAsync(bool forward, CancellationToken ct)
    {
        if (IsEmpty)
            return SearchPosition.NotFound;

        var result = forward
            ? await dispatchAsync(() => document.FindNext(_pattern, matchCase: false, _useRegex), ct).ConfigureAwait(false)
            : await dispatchAsync(() => document.FindPrevious(_pattern, matchCase: false, _useRegex), ct).ConfigureAwait(false);

        return !result.Found
            ? SearchPosition.NotFound
            : new SearchPosition { Current = result.CurrentIndex, Total = _totalMatches };
    }

    private bool UpdatePattern(string? pattern, bool useRegex)
    {
        var newPattern = pattern ?? string.Empty;
        if (newPattern == _pattern && useRegex == _useRegex)
            return false;

        _pattern = newPattern;
        _useRegex = useRegex;
        _totalMatches = 0;
        return true;
    }

    private void ResetPattern()
    {
        _pattern = string.Empty;
        _useRegex = false;
        _totalMatches = 0;
    }
}
