using System.Diagnostics;
using System.Windows.Forms;
using ScintillaNET;
using ZLogger.Scintilla.Controls;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using ZLogger.Scintilla.Rendering;
// ReSharper disable RedundantCast
namespace ZLogger.Scintilla.Services;

/// <summary>
/// The document and its view. Positions here are always bytes, matching Scintilla's messages. All methods
/// except <see cref="CountMatchesAsync"/> must be called on the UI thread.
/// </summary>
internal sealed class ScintillaDocument : IDisposable
{
    private readonly LogScintillaControl _scintilla;
    private readonly ScintillaSearchService _searchService;
    private readonly ScintillaInteractionService _interactionService;
    private readonly ScintillaStyleService _styleService;
    private readonly List<TokenRange> _tokenRanges = new(LogConstants.TokenRangesCapacity);
    private readonly Dictionary<int, int> _linkStyleByBaseStyleId = new();
    private readonly bool _autoScroll;
    private long _lastSearchRefreshTick;
    private bool _batchUpdateActive;
    private bool _lineNumbersEnabled;

    /// <summary>True until the user scrolls away from the end, and again once they scroll back to it.</summary>
    private bool _followTail = true;

    public ScintillaDocument(
        LogScintillaControl scintilla,
        ScintillaOptions options,
        IScintillaTheme initialTheme)
    {
        _scintilla = scintilla;
        _autoScroll = options.AutoScroll;
        _searchService = new ScintillaSearchService(_scintilla);
        _interactionService = new ScintillaInteractionService(_scintilla, options, _tokenRanges);
        _styleService = new ScintillaStyleService(_scintilla, _tokenRanges, _linkStyleByBaseStyleId);
        ConfigureControl();
        _searchService.ApplyTheme(initialTheme);
        _scintilla.MouseUp += OnMouseUp;
        _scintilla.MouseMove += OnMouseMove;
        _scintilla.MouseLeave += OnMouseLeave;
        _scintilla.MouseWheel += OnMouseWheel;
        _scintilla.UpdateUI += OnUpdateUi;
        _scintilla.SizeChanged += OnSizeChanged;
        _scintilla.Painted += OnPainted;
        _scintilla.UserScrolled += OnUserScrolled;
    }

    public void ApplyThemeStyles(LogRenderer renderStrategy, IScintillaTheme theme)
    {
        _scintilla.StyleResetDefault();
        renderStrategy.ApplyThemeStyles(_scintilla, theme);
        _styleService.RefreshLinkStylesFromBaseStyles();
        _styleService.ResetColorizerStyles();
        _searchService.ApplyTheme(theme);
        RefreshSearchHighlightIfActive();
    }

    /// <summary>Makes the document writable for a batch update. Must be paired with <see cref="EndBatchUpdate"/>.</summary>
    public void BeginBatchUpdate()
    {
        _batchUpdateActive = true;
        Send(LogConstants.SciSetReadOnly);
    }

    /// <summary>Ends a batch update: makes the document read-only again and shows the new text.</summary>
    public void EndBatchUpdate(bool documentChanged)
    {
        if (!_batchUpdateActive)
            return;
        _batchUpdateActive = false;

        if (documentChanged)
        {
            ScrollToTailIfFollowing();
            if (_lineNumbersEnabled)
                _scintilla.Margins[0].Width = CalculateLineNumberWidth();
        }

        Send(LogConstants.SciSetReadOnly, 1);
    }

    /// <summary>
    /// Appends a prepared batch. Only performs UI-thread work: text append, styling, and token registration.
    /// Must be called between <see cref="BeginBatchUpdate"/> and <see cref="EndBatchUpdate"/>.
    /// </summary>
    public void ApplyPreparedBatch(in PreparedBatch batch)
    {
        if (batch.Text.Count <= 0)
            return;

        var appendStart = (int)Send(LogConstants.SciGetLength);
        AppendBytesToScintilla(batch.Text.Array, batch.Text.Count);
        _styleService.ApplyPreparedStyles(appendStart, batch.Text.Count,
            batch.Styles.Array, batch.Styles.Count,
            batch.Tokens.Array, batch.Tokens.Count);
        _interactionService.TrimTokenRangesIfNeeded(LogConstants.DefaultMaxTokenRanges);
    }

    /// <summary>Refreshes search highlights. Called once per drain, not per batch.</summary>
    public void RefreshSearch() => RefreshSearchHighlightIfActive();

    public void TrimHeadLines(int linesToRemove)
    {
        var lineCount = GetLineCount();
        var lastLineToRemove = Math.Min(linesToRemove, lineCount - 1);
        if (lastLineToRemove <= 0)
            return;

        var deletedBytes = (int)Send(LogConstants.SciPositionFromLine, lastLineToRemove);
        if (deletedBytes <= 0)
            return;

        if (!_batchUpdateActive)
            Send(LogConstants.SciSetReadOnly);
        Send(LogConstants.SciDeleteRange, 0, deletedBytes);
        if (!_batchUpdateActive)
            Send(LogConstants.SciSetReadOnly, 1);

        AdjustTokenRangesAfterTrim(deletedBytes);
        _searchService.OnHeadTrimmed(deletedBytes);
    }

    private void AdjustTokenRangesAfterTrim(int deletedBytes)
    {
        if (_tokenRanges.Count == 0)
            return;

        var writeIndex = 0;
        for (var i = 0; i < _tokenRanges.Count; i++)
        {
            var range = _tokenRanges[i];
            if (range.Start < deletedBytes)
                continue;

            _tokenRanges[writeIndex++] = range.WithOffset(-deletedBytes);
        }

        if (writeIndex < _tokenRanges.Count)
            _tokenRanges.RemoveRange(writeIndex, _tokenRanges.Count - writeIndex);
    }

    public int GetLineCount() => (int)Send(LogConstants.SciGetLineCount);

    /// <summary>
    /// Replaces the document so that its memory is released. Assigning <see cref="Document.Empty"/> makes
    /// Scintilla release the old document and create a new one, which keeps the reference counts balanced.
    /// </summary>
    public void Clear()
    {
        _scintilla.Document = Document.Empty;
        Send(LogConstants.SciSetUndoCollection);
        _scintilla.EmptyUndoBuffer();
        Send(LogConstants.SciSetReadOnly, _batchUpdateActive ? 0 : 1);
        ForgetDocumentContent();
    }

    /// <summary>Empties the document ahead of a replay. Keeps its allocation, because similar text is about to return.</summary>
    public void ResetForReplay()
    {
        var wasActive = _batchUpdateActive;
        if (!wasActive)
            Send(LogConstants.SciSetReadOnly);
        Send(LogConstants.SciClearAll);
        if (!wasActive)
            Send(LogConstants.SciSetReadOnly, 1);
        ForgetDocumentContent();
    }

    private void ForgetDocumentContent()
    {
        _tokenRanges.Clear();
        _followTail = true;
        _searchService.OnDocumentReset();
        RefreshSearchHighlightIfActive();
    }

    public SearchMatch FindNext(string pattern, SearchOptions options)
    {
        var match = _searchService.FindNext(pattern, options);
        UpdateFollowTailAfterMove(match);
        return match;
    }

    public SearchMatch FindPrevious(string pattern, SearchOptions options)
    {
        var match = _searchService.FindPrevious(pattern, options);
        UpdateFollowTailAfterMove(match);
        return match;
    }

    /// <summary>Stays on the match while logs arrive. Scrolling back to the end resumes following.</summary>
    private void UpdateFollowTailAfterMove(SearchMatch match)
    {
        if (match.Found)
            _followTail = IsAtEnd();
    }

    public void HighlightSearch(string pattern, SearchOptions options)
        => _searchService.HighlightSearch(pattern, options);

    /// <summary>Raised when replacing the document ended a find in selection.</summary>
    public event Action? SearchScopeLost
    {
        add => _searchService.ScopeLost += value;
        remove => _searchService.ScopeLost -= value;
    }

    /// <summary>
    /// Limits searches to the current selection. Returns whether a range is now in effect, which needs
    /// something to be selected.
    /// </summary>
    public bool SetSearchScope(bool enabled)
    {
        var start = (int)Send(LogConstants.SciGetSelectionStart);
        var end = (int)Send(LogConstants.SciGetSelectionEnd);
        if (!enabled || end <= start)
        {
            _searchService.SetScope(false);
            return false;
        }

        _searchService.SetScope(true, start, end);
        return true;
    }

    public int RankOfCurrentMatch() => _searchService.RankOfCurrentMatch();

    public int DocumentLength => (int)Send(LogConstants.SciGetLength);

    public Task<SearchMatch> FindNextAsync(string pattern, SearchOptions options, CancellationToken cancellationToken) =>
        RunOnUiAsync(() => _scintilla.IsDisposed ? SearchMatch.NotFound : FindNext(pattern, options), cancellationToken);

    public Task<SearchMatch> FindPreviousAsync(string pattern, SearchOptions options, CancellationToken cancellationToken) =>
        RunOnUiAsync(() => _scintilla.IsDisposed ? SearchMatch.NotFound : FindPrevious(pattern, options), cancellationToken);

    /// <summary>
    /// Counts on the UI thread with Scintilla's own search, the same engine navigation uses, in slices short
    /// enough not to stall the UI. A snapshot searched on another thread would need a second regex engine,
    /// and two engines do not agree on what a pattern matches.
    /// </summary>
    public async Task<int> CountMatchesAsync(
        string pattern,
        SearchOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern) || _scintilla.IsDisposed || !_scintilla.IsHandleCreated)
            return 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (finished, count) = await RunOnUiAsync(() =>
            {
                // A slice posted before the count was cancelled still runs, and must not bring back a pattern
                // that a newer search has replaced.
                if (cancellationToken.IsCancellationRequested)
                    return (true, 0);

                _searchService.SetActiveSearch(pattern, options);
                var done = _searchService.CountSlice(LogConstants.CountSliceBudgetMs, out var total);
                return (done, total);
            }, cancellationToken).ConfigureAwait(false);

            if (finished)
                return count;
        }
    }

    public void Dispose()
    {
        _styleService.ReleaseBuffer();
        _scintilla.MouseUp -= OnMouseUp;
        _scintilla.MouseMove -= OnMouseMove;
        _scintilla.MouseLeave -= OnMouseLeave;
        _scintilla.MouseWheel -= OnMouseWheel;
        _scintilla.UpdateUI -= OnUpdateUi;
        _scintilla.SizeChanged -= OnSizeChanged;
        _scintilla.Painted -= OnPainted;
        _scintilla.UserScrolled -= OnUserScrolled;
        _scintilla.Dispose();
    }

    public void SetLineNumbers(bool enabled)
    {
        _lineNumbersEnabled = enabled;
        if (enabled)
        {
            _scintilla.Margins[0].Type = MarginType.Number;
            _scintilla.Margins[0].Width = CalculateLineNumberWidth();
        }
        else
        {
            _scintilla.Margins[0].Width = 0;
        }
    }

    private int CalculateLineNumberWidth()
    {
        var lineCount = Math.Max(GetLineCount(), 1);
        var digits = (int)Math.Floor(Math.Log10(lineCount) + 1);
        digits = Math.Max(digits, 4);
        return _scintilla.TextWidth(Style.LineNumber, new string('9', digits + 1));
    }

    private void ConfigureControl()
    {
        _scintilla.WrapMode = WrapMode.Word;
        _scintilla.ReadOnly = true;
        Send(LogConstants.SciSetUndoCollection);
        _scintilla.EmptyUndoBuffer();
        _scintilla.IndentationGuides = IndentView.None;
        Send(LogConstants.SciSetLayoutCache, LogConstants.ScCachePage);
        _scintilla.Margins[0].Width = 0;
        _scintilla.Margins[1].Width = 0;
        _scintilla.Margins[2].Width = 0;

    }

    private void ScrollToTailIfFollowing()
    {
        // Moving the view while the mouse drags a selection would select text the user never pointed at.
        if (_autoScroll && _followTail && !_scintilla.Capture)
            Send(LogConstants.SciScrollToEnd);
    }

    /// <summary>
    /// True when the last row is in view. Rows, not lines: with word wrap, one line takes several rows,
    /// and Scintilla only learns how many when it lays the line out.
    /// </summary>
    private bool IsAtEnd()
    {
        var lastLine = GetLineCount() - 1;
        var rows = Send(LogConstants.SciVisibleFromDocLine, lastLine) + 1;
        return Send(LogConstants.SciGetFirstVisibleLine) + Send(LogConstants.SciLinesOnScreen) >= rows;
    }

    private void OnMouseUp(object? sender, MouseEventArgs e) => _interactionService.HandleMouseUp(e);

    private void OnMouseMove(object? sender, MouseEventArgs e) => _interactionService.HandleMouseMove(e);

    private void OnMouseLeave(object? sender, EventArgs e) => _interactionService.HandleMouseLeave();

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_searchService.HasActiveSearch)
            RefreshSearchHighlightIfActive(force: true);
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (_searchService.HasActiveSearch)
            RefreshSearchHighlightIfActive(force: true);
    }

    private void OnUpdateUi(object? sender, UpdateUIEventArgs e)
    {
        if (!_searchService.HasActiveSearch)
            return;

        if ((e.Change & (UpdateChange.VScroll | UpdateChange.HScroll)) == 0)
            return;

        RefreshSearchHighlightIfActive(force: true);
    }

    private void OnUserScrolled(object? sender, EventArgs e) => _followTail = IsAtEnd();

    /// <summary>
    /// Scintilla measures wrapped lines when it paints them, and only then does the document grow. Scrolling
    /// to the end before that stops short, so each paint gets the chance to correct it.
    /// </summary>
    private void OnPainted(object? sender, EventArgs e)
    {
        if (_autoScroll && _followTail && !_batchUpdateActive && !IsAtEnd())
            ScrollToTailIfFollowing();
    }

    private void RefreshSearchHighlightIfActive(bool force = false)
    {
        if (!_searchService.HasActiveSearch)
            return;

        var now = (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        if (!force && now - _lastSearchRefreshTick < LogConstants.SearchRefreshThrottleMs)
            return;

        _lastSearchRefreshTick = now;
        _searchService.RefreshHighlightIfActive();
    }

    private Task<T> RunOnUiAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
            : default;

        try
        {
            _scintilla.BeginInvoke(() =>
            {
                try
                {
                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    // Disposed here or in the catch below, never both: the catch runs only if nothing was posted.
                    // ReSharper disable once AccessToDisposedClosure
                    registration.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            registration.Dispose();
            tcs.TrySetException(ex);
        }

        return tcs.Task;
    }

    private unsafe void AppendBytesToScintilla(byte[] buffer, int count)
    {
        fixed (byte* ptr = buffer)
        {
            _scintilla.DirectMessage(LogConstants.SciAppendText, (IntPtr)count, (IntPtr)ptr);
        }
    }

    private long Send(int message, long wParam = 0, long lParam = 0) =>
        (long)_scintilla.DirectMessage(message, (IntPtr)wParam, (IntPtr)lParam);
}
