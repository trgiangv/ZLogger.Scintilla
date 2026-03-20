using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScintillaNET;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using ZLogger.Scintilla.Rendering;
// ReSharper disable RedundantCast
namespace ZLogger.Scintilla.Services;

internal sealed class ScintillaDocument : IDisposable
{
    private readonly ScintillaNET.Scintilla _scintilla;
    private readonly ScintillaSearchService _searchService;
    private readonly ScintillaInteractionService _interactionService;
    private readonly ScintillaStyleService _styleService;
    private readonly List<TokenRange> _tokenRanges = new(LogConstants.TokenRangesCapacity);
    private readonly Dictionary<int, int> _linkStyleByBaseStyleId = new();
    private long _lastSearchRefreshTick;

    public ScintillaDocument(
        ScintillaNET.Scintilla scintilla,
        ScintillaOptions options,
        IScintillaTheme initialTheme)
    {
        _scintilla = scintilla;
        _searchService = new ScintillaSearchService(_scintilla);
        _interactionService = new ScintillaInteractionService(_scintilla, options, _tokenRanges);
        _styleService = new ScintillaStyleService(_scintilla, options, _tokenRanges, _linkStyleByBaseStyleId);
        ConfigureControl();
        _searchService.ApplyTheme(initialTheme);
        _scintilla.DoubleClick += OnDoubleClick;
        _scintilla.MouseUp += OnMouseUp;
        _scintilla.MouseMove += OnMouseMove;
        _scintilla.MouseLeave += OnMouseLeave;
        _scintilla.MouseWheel += OnMouseWheel;
        _scintilla.UpdateUI += OnUpdateUi;
        _scintilla.SizeChanged += OnSizeChanged;
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

    private bool _batchUpdateActive;
    private WrapMode _savedWrapMode;

    /// <summary>
    /// Freezes Scintilla for a batch update cycle: disables word-wrap and sets mutable mode.
    /// Must be paired with <see cref="EndBatchUpdate"/>.
    /// </summary>
    public void BeginBatchUpdate()
    {
        _batchUpdateActive = true;
        _savedWrapMode = _scintilla.WrapMode;
        if (_savedWrapMode != WrapMode.None)
            _scintilla.WrapMode = WrapMode.None;
        if (_scintilla.ReadOnly)
            _scintilla.ReadOnly = false;
    }

    /// <summary>
    /// Thaws Scintilla after a batch update cycle: restores word-wrap, scrolls once, re-enables read-only.
    /// </summary>
    public void EndBatchUpdate(bool autoScroll)
    {
        if (!_batchUpdateActive)
            return;
        _batchUpdateActive = false;

        if (_savedWrapMode != WrapMode.None)
            _scintilla.WrapMode = _savedWrapMode;

        if (autoScroll)
        {
            var docEnd = (int)_scintilla.DirectMessage(LogConstants.SciGetLength, IntPtr.Zero, IntPtr.Zero);
            _scintilla.DirectMessage(LogConstants.SciGotoPos, (IntPtr)docEnd, IntPtr.Zero);
            _scintilla.ScrollCaret();
        }

        if (_scintilla.Margins[0].Width > 0)
            _scintilla.Margins[0].Width = CalculateLineNumberWidth();

        _scintilla.ReadOnly = true;
        _searchService.InvalidateMatchIndex();
    }

    /// <summary>
    /// Applies a pre-prepared batch to the Scintilla control. Only performs UI-thread work:
    /// text append, style resolution, and token registration.
    /// Must be called between <see cref="BeginBatchUpdate"/> and <see cref="EndBatchUpdate"/>.
    /// </summary>
    public void ApplyPreparedBatch(PreparedBatch batch)
    {
        if (batch.Text.Count <= 0)
            return;

        var appendStart = (int)_scintilla.DirectMessage(LogConstants.SciGetLength, IntPtr.Zero, IntPtr.Zero);
        AppendBytesToScintilla(batch.Text.Array, batch.Text.Count);
        _styleService.ApplyPreparedStyles(appendStart, batch.Text.Count,
            batch.Styles.Array, batch.Styles.Count,
            batch.Tokens.Array, batch.Tokens.Count);
        _interactionService.TrimTokenRangesIfNeeded(LogConstants.DefaultMaxTokenRanges);
    }

    /// <summary>
    /// Refreshes search highlights after one or more batches have been applied.
    /// Called once per drain cycle to avoid redundant work.
    /// </summary>
    public void InvalidateAndRefreshSearch()
    {
        RefreshSearchHighlightIfActive();
    }

    public void TrimHeadLines(int linesToRemove)
    {
        if (linesToRemove <= 0 || _scintilla.Lines.Count == 0)
            return;

        _searchService.InvalidateMatchIndex();

        var wasReadOnly = BeginMutableSection();
        int deletedBytes;
        try
        {
            var lastLineToRemove = Math.Min(linesToRemove, _scintilla.Lines.Count - 1);
            if (lastLineToRemove <= 0)
                return;

            deletedBytes = _scintilla.Lines[lastLineToRemove].Position;
            if (deletedBytes > 0)
                _scintilla.DeleteRange(0, deletedBytes);
        }
        finally
        {
            EndMutableSection(wasReadOnly);
        }

        AdjustTokenRangesAfterTrim(deletedBytes);
        RefreshSearchHighlightIfActive();
    }

    private void AdjustTokenRangesAfterTrim(int deletedBytes)
    {
        if (deletedBytes <= 0)
            return;

        if (_tokenRanges.Count == 0)
            return;

        var writeIndex = 0;
        for (var i = 0; i < _tokenRanges.Count; i++)
        {
            var range = _tokenRanges[i];
            var rangeEnd = range.Start + range.Length;
            if (rangeEnd <= deletedBytes)
                continue;

            if (range.Start < deletedBytes)
                continue;

            _tokenRanges[writeIndex++] = range.WithOffset(-deletedBytes);
        }

        if (writeIndex < _tokenRanges.Count)
            _tokenRanges.RemoveRange(writeIndex, _tokenRanges.Count - writeIndex);
    }

    public int GetLineCount() => _scintilla.Lines.Count;

    public void Clear()
    {
        _searchService.InvalidateMatchIndex();
        var wasReadOnly = BeginMutableSection();
        try
        {
            _scintilla.ClearAll();
            _scintilla.EmptyUndoBuffer();
            _tokenRanges.Clear();
        }
        finally
        {
            EndMutableSection(wasReadOnly);
        }

        RefreshSearchHighlightIfActive();
    }

    public SearchMatch FindNext(string pattern, bool matchCase, bool useRegex)
        => _searchService.FindNext(pattern, matchCase, useRegex);

    public SearchMatch FindPrevious(string pattern, bool matchCase, bool useRegex)
        => _searchService.FindPrevious(pattern, matchCase, useRegex);

    public SearchMatch HighlightSearch(string pattern, bool matchCase, bool useRegex)
        => _searchService.HighlightSearch(pattern, matchCase, useRegex);

    public async Task<int> CountMatchesAsync(
        string pattern,
        bool matchCase,
        bool useRegex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return 0;

        if (_scintilla.IsDisposed)
            return 0;

        var snapshot = await CaptureTextSnapshotAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(snapshot))
            return 0;

        var count = await Task.Run(() => CountMatches(snapshot, pattern, matchCase, useRegex, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        if (!cancellationToken.IsCancellationRequested && !_scintilla.IsDisposed)
        {
            if (_scintilla.InvokeRequired)
                _scintilla.BeginInvoke(_searchService.BuildMatchIndex);
            else
                _searchService.BuildMatchIndex();
        }

        return count;
    }

    public void Dispose()
    {
        _styleService.ReleaseBuffer();
        _scintilla.DoubleClick -= OnDoubleClick;
        _scintilla.MouseUp -= OnMouseUp;
        _scintilla.MouseMove -= OnMouseMove;
        _scintilla.MouseLeave -= OnMouseLeave;
        _scintilla.MouseWheel -= OnMouseWheel;
        _scintilla.UpdateUI -= OnUpdateUi;
        _scintilla.SizeChanged -= OnSizeChanged;
        _scintilla.Dispose();
    }

    public void SetLineNumbers(bool enabled)
    {
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
        var lineCount = Math.Max(_scintilla.Lines.Count, 1);
        var digits = (int)Math.Floor(Math.Log10(lineCount) + 1);
        digits = Math.Max(digits, 4);
        return _scintilla.TextWidth(Style.LineNumber, new string('9', digits + 1));
    }

    private void ConfigureControl()
    {
        _scintilla.WrapMode = WrapMode.Word;
        _scintilla.ReadOnly = true;
        _scintilla.DirectMessage(LogConstants.SciSetUndoCollection, IntPtr.Zero, IntPtr.Zero);
        _scintilla.EmptyUndoBuffer();
        _scintilla.IndentationGuides = IndentView.None;
        _scintilla.Margins[0].Width = 0;
        _scintilla.Margins[1].Width = 0;
        _scintilla.Margins[2].Width = 0;

        var indicator = _scintilla.Indicators[StyleMapper.TokenIndicatorStyleId];
        indicator.Style = IndicatorStyle.RoundBox;
        indicator.ForeColor = StyleMapper.TokenIndicatorColor;
        indicator.Under = StyleMapper.TokenIndicatorUnderText;

        var hotspotStyle = _scintilla.Styles[StyleMapper.LinkHotspotStyleId];
        hotspotStyle.Hotspot = true;
        hotspotStyle.Underline = true;
    }

    private bool BeginMutableSection()
    {
        var wasReadOnly = _scintilla.ReadOnly;
        if (wasReadOnly)
            _scintilla.ReadOnly = false;
        return wasReadOnly;
    }

    private void EndMutableSection(bool wasReadOnly)
    {
        if (wasReadOnly)
            _scintilla.ReadOnly = true;
    }


    private void OnDoubleClick(object? sender, EventArgs e) => _interactionService.HandleDoubleClick();

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

    private Task<string> CaptureTextSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_scintilla.InvokeRequired)
            return Task.FromResult(_scintilla.Text);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
            : default;
        
        var holder = registration; // To avoid closure capture of the struct in the lambda
        try
        {
            _scintilla.BeginInvoke(() =>
            {
                try
                {
                    tcs.TrySetResult(_scintilla.IsDisposed ? string.Empty : _scintilla.Text);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    holder.Dispose();
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
        if (count <= 0)
            return;

        fixed (byte* ptr = buffer)
        {
            _scintilla.DirectMessage(LogConstants.SciAppendText, (IntPtr)count, (IntPtr)ptr);
        }
    }

    private static int CountMatches(string text, string pattern, bool matchCase, bool useRegex, CancellationToken cancellationToken)
        => useRegex
            ? CountRegexMatches(text, pattern, matchCase, cancellationToken)
            : CountPlainMatches(text, pattern, matchCase, cancellationToken);

    private static int CountRegexMatches(string text, string pattern, bool matchCase, CancellationToken cancellationToken)
    {
        var options = System.Text.RegularExpressions.RegexOptions.CultureInvariant;
        if (!matchCase)
            options |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;

        var count = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, pattern, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (match.Success && match.Length > 0)
                count++;
        }

        return count;
    }

    private static int CountPlainMatches(string text, string pattern, bool matchCase, CancellationToken cancellationToken)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = text.IndexOf(pattern, index, comparison);
            if (found < 0)
                break;

            count++;
            index = found + Math.Max(pattern.Length, 1);
        }

        return count;
    }

}
