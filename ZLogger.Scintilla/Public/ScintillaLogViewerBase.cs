using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;
using ZLogger.Scintilla.Rendering;
using ZLogger.Scintilla.Services;
// ReSharper disable ForCanBeConvertedToForeach
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif
namespace ZLogger.Scintilla.Public;

public abstract class ScintillaLogViewerBase : IDisposable
{
    private readonly ScintillaDocument _document;
    private readonly Action<Action> _dispatchInvoke;
    private readonly Action<Action> _dispatchBeginInvoke;
    private readonly LogRenderer _renderStrategy;
    private readonly BatchPreparer _batchPreparer;
    private readonly ScintillaOptions _options;
    private readonly LogIngestPump _ingestPump;
    private readonly LogIngestMetrics _metrics = new();
    private readonly LogHistoryBuffer _history;
    private readonly ConcurrentQueue<PreparedBatch> _renderQueue = new();
    private readonly List<LogEntry> _filterBuffer;
    private readonly object _stateGate = new();
    private readonly SearchCoordinator _search;
    private readonly bool _autoScroll;
    private ScintillaLogProcessor? _processor;
    private List<LogEntry> _batchBuffer;
    private List<Action>? _disposeActions;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private long _clearEpoch;
    private int _uiDrainScheduled;
    private CancellationTokenSource? _filterCts;
    private long _filterVersion;
    private int _minimumFilterLevel = (int)LogLevel.Trace;
    private IScintillaTheme _currentTheme;
    private bool _disposed;

    public ScintillaNET.Scintilla ScintillaControl { get; }

    protected ScintillaLogViewerBase(Action<ScintillaOptions>? configure)
    {
        var resolved = new ScintillaOptions();
        configure?.Invoke(resolved);
        _options = resolved;
        _currentTheme = resolved.Theme;
        _autoScroll = resolved.AutoScroll;

        ScintillaControl = new ScintillaNET.Scintilla
        {
            Dock = DockStyle.Fill,
            BorderStyle = ScintillaNET.BorderStyle.None,
        };

        _dispatchInvoke = action =>
        {
            if (ScintillaControl.IsDisposed) return;
            if (!ScintillaControl.IsHandleCreated)
            {
                if (!ScintillaControl.InvokeRequired) action();
                return;
            }
            if (!ScintillaControl.InvokeRequired) action();
            else ScintillaControl.Invoke(action);
        };
        _dispatchBeginInvoke = action =>
        {
            if (ScintillaControl.IsDisposed || !ScintillaControl.IsHandleCreated) return;
            ScintillaControl.BeginInvoke(action);
        };

        _document = new ScintillaDocument(ScintillaControl, resolved, _currentTheme);
        _renderStrategy = new LogRenderer(resolved.FontFamily, resolved.FontSize, resolved);
        _batchPreparer = new BatchPreparer(_renderStrategy, resolved.EnableTokenLinks);
        _batchBuffer = new List<LogEntry>(resolved.MaxBatchSize);
        _filterBuffer = new List<LogEntry>(resolved.MaxBatchSize);
        _ingestPump = new LogIngestPump(resolved);

        var maxHistory = resolved.DisableHistory ? 0 : Math.Max(0, resolved.MaxHistoryEntries);
        _history = new LogHistoryBuffer(_metrics, maxHistory);
        _search = new SearchCoordinator(_document, Dispatch, DispatchSearchAsync);

        ApplyInitialStylesDeferred();
        ApplyNativeThemeToControl();
    }

    public abstract object HostElement { get; }
    protected abstract void DisposeHostResources();

    public void Start()
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpAsync(_cts.Token), _cts.Token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? pumpTask;
        lock (_stateGate)
        {
            cts = _cts;
            pumpTask = _pumpTask;
            _cts = null;
            _pumpTask = null;
        }
        if (cts == null) return;
        cts.Cancel();
        try
        {
            pumpTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        finally
        {
            cts.Dispose();
        }
    }

    public void Clear()
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref _clearEpoch);
        _history.DrainAll(static e => e.ReleaseBuffer());
        _ingestPump.Drain(static e => e.ReleaseBuffer());
        DrainAndDiscardRenderQueue();
        _metrics.ResetOnClear();
        Dispatch(() => _document.Clear());
    }

    public void SetTheme(IScintillaTheme theme)
    {
        ThrowIfDisposed();
        if (ReferenceEquals(_currentTheme, theme)) return;
        _currentTheme = theme;
        RefreshStyles();
        ApplyNativeTheme();
    }

    public void SetPrettyJson(bool enabled)
    {
        ThrowIfDisposed();
        _processor?.SetPrettyJson(enabled);
        _renderStrategy.SetPrettyJson(enabled);
        ReapplyCurrentFilter();
    }

    public void SetLineNumbers(bool enabled)
    {
        ThrowIfDisposed();
        Dispatch(() => _document.SetLineNumbers(enabled));
    }

    public void SetJsonDepth(int depth)
    {
        ThrowIfDisposed();
        _processor?.SetJsonDepth(depth);
    }

    internal void AttachProcessor(ScintillaLogProcessor processor)
    {
        _processor = processor;
        processor.SetJsonDepth(_options.JsonDecomposeDepth);
        processor.SetCollectionLimit(_options.JsonDecomposeMaxCollection);
        processor.SetCustomSerializer(_options.CustomSerializer);
    }

    public void SetFilter(LogLevel minimumLevel)
    {
        ThrowIfDisposed();
        ApplyFilterLevel(minimumLevel);
    }

    public void HighlightSearch(string pattern, bool useRegex = false) { ThrowIfDisposed(); _search.HighlightSearch(pattern, useRegex); }
    public void ClearSearch() { ThrowIfDisposed(); _search.ClearSearch(); }
    public SearchPosition FindNext() { ThrowIfDisposed(); return _search.FindNext(); }
    public Task<SearchPosition> FindNextAsync(CancellationToken ct = default) { ThrowIfDisposed(); return _search.FindNextAsync(ct); }
    public SearchPosition FindPrevious() { ThrowIfDisposed(); return _search.FindPrevious(); }
    public Task<SearchPosition> FindPreviousAsync(CancellationToken ct = default) { ThrowIfDisposed(); return _search.FindPreviousAsync(ct); }
    public Task<int> CountMatchesAsync(CancellationToken ct = default) { ThrowIfDisposed(); return _search.CountMatchesAsync(ct); }

    public long RenderedMessages => _metrics.RenderedMessages;
    public long DroppedMessages => _metrics.DroppedMessages;
    public long PendingEntryCount => _metrics.PendingEntryCount;
    public long HistoryEntries => _metrics.HistoryEntries;

    internal bool TryPost(LogEntry entry)
    {
        if (_disposed)
        {
            entry.ReleaseBuffer();
            return false;
        }
        if (_ingestPump.TryPost(entry))
        {
            _metrics.RecordAccepted();
            return true;
        }
        _metrics.RecordDrop();
        entry.ReleaseBuffer();
        return false;
    }

    public void RegisterDisposeAction(Action action)
    {
        _disposeActions ??= [];
        _disposeActions.Add(action);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (!disposing) return;

        if (_disposeActions is not null)
        {
            foreach (var action in _disposeActions)
                action();
            _disposeActions = null;
        }

        ScintillaControl.HandleCreated -= OnHandleCreatedApplyStyles;
        ScintillaControl.HandleCreated -= OnHandleCreatedApplyTheme;
        Stop();
        CancelFilterReplay();
        _ingestPump.Drain(static e => e.ReleaseBuffer());
        _history.DrainAll(static e => e.ReleaseBuffer());
        DrainAndDiscardRenderQueue();
        _ingestPump.Complete();
        _batchPreparer.Dispose();
        _document.Dispose();
        DisposeHostResources();
    }

    internal static LogLevel ClampFilterLevel(LogLevel level)
    {
        return level switch
        {
            < LogLevel.Trace => LogLevel.Trace,
            > LogLevel.None => LogLevel.None,
            _ => level
        };
    }

    private void ApplyFilterLevel(LogLevel minimumLevel)
    {
        var clamped = ClampFilterLevel(minimumLevel);
        if ((LogLevel)Volatile.Read(ref _minimumFilterLevel) == clamped) return;
        Interlocked.Exchange(ref _minimumFilterLevel, (int)clamped);
        ReplayFilteredHistory(clamped);
    }

    private void ReapplyCurrentFilter()
    {
        var currentMinimumLevel = (LogLevel)Volatile.Read(ref _minimumFilterLevel);
        if (currentMinimumLevel > LogLevel.Trace)
            ReplayFilteredHistory(currentMinimumLevel);
    }

    private void ReplayFilteredHistory(LogLevel minimumLevel)
    {
        var currentVersion = Interlocked.Increment(ref _filterVersion);
        var clearEpoch = Volatile.Read(ref _clearEpoch);
        if (!ScintillaControl.IsHandleCreated)
            return;
        var nextCts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _filterCts, nextCts);
        previousCts?.Cancel();
        previousCts?.Dispose();
        var token = nextCts.Token;
        Task.Run(() => ExecuteFilterReplay(minimumLevel, clearEpoch, currentVersion, token), token);
    }

    private void ExecuteFilterReplay(LogLevel minimumLevel, long clearEpoch, long currentVersion, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        var replayPreparer = new BatchPreparer(_renderStrategy, _options.EnableTokenLinks);
        try
        {
            var batches = new List<PreparedBatch>();
            foreach (var chunk in _history.CaptureChunkedSnapshot(minimumLevel, cancellationToken: token))
            {
                if (token.IsCancellationRequested) break;
                batches.Add(replayPreparer.Prepare(chunk, clearEpoch, entriesToRelease: null));
            }
            if (token.IsCancellationRequested)
            {
                foreach (var b in batches) b.ReturnBuffers();
                return;
            }
            _dispatchBeginInvoke(() => ApplyFilteredReplay(batches, currentVersion, clearEpoch));
        }
        finally { replayPreparer.Dispose(); }
    }

    private void ApplyFilteredReplay(List<PreparedBatch> batches, long expectedVersion, long expectedEpoch)
    {
        if (_disposed || expectedVersion != Volatile.Read(ref _filterVersion) || expectedEpoch != Volatile.Read(ref _clearEpoch))
        {
            foreach (var b in batches) b.ReturnBuffers();
            return;
        }
        _document.Clear();
        var totalRendered = 0;
        _document.BeginBatchUpdate();
        try
        {
            foreach (var batch in batches)
            {
                try
                {
                    if (batch.EntryCount <= 0) continue;
                    _document.ApplyPreparedBatch(batch);
                    totalRendered += batch.EntryCount;
                }
                finally { batch.ReturnBuffers(); }
            }
        }
        finally { _document.EndBatchUpdate(totalRendered > 0 && _autoScroll); }
        if (totalRendered > 0) _metrics.RecordRendered(totalRendered);
        TrimDocumentIfNeeded();
        _document.InvalidateAndRefreshSearch();
    }

    private void CancelFilterReplay()
    {
        var cts = Interlocked.Exchange(ref _filterCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
#if NET8_0_OR_GREATER
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.FlushIntervalMs));
        while (!cancellationToken.IsCancellationRequested)
        {
            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            while (ProcessBatch()) { /* ignored */ }
        }
#else
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_options.FlushIntervalMs, cancellationToken).ConfigureAwait(false);
            while (ProcessBatch()) { /* ignored */ }
        }
#endif
    }

    private bool ProcessBatch()
    {
        var batchEpoch = Volatile.Read(ref _clearEpoch);
        var minimumLevel = (LogLevel)Volatile.Read(ref _minimumFilterLevel);
        var readCount = _ingestPump.TryReadBatch(_batchBuffer);
        if (readCount <= 0) return false;

        _metrics.DecrementBacklog(readCount);
        if (batchEpoch != Volatile.Read(ref _clearEpoch))
        {
            ReleaseBatchEntries(_batchBuffer);
            return true;
        }
        if (_history.IsEnabled)
        {
            _history.EnqueueBatch(_batchBuffer);
            _history.TrimIfNeeded();
        }
        var visibleEntries = FilterBatch(minimumLevel);
        if (visibleEntries.Count == 0)
        {
            if (!_history.IsEnabled) ReleaseBatchEntries(_batchBuffer);
            return true;
        }
        List<LogEntry>? entriesToRelease = null;
        if (!_history.IsEnabled)
        {
            entriesToRelease = visibleEntries.Count == _batchBuffer.Count
                ? visibleEntries
                : _batchBuffer;
        }
        var prepared = _batchPreparer.Prepare(visibleEntries, batchEpoch, entriesToRelease);
        EnqueuePreparedBatch(prepared);
        return true;
    }

    private List<LogEntry> FilterBatch(LogLevel minimumLevel)
    {
        if (minimumLevel <= LogLevel.Trace) return ConsumeBatchBuffer();
        _filterBuffer.Clear();
        for (var i = 0; i < _batchBuffer.Count; i++)
        {
            if (_batchBuffer[i].Level >= minimumLevel)
                _filterBuffer.Add(_batchBuffer[i]);
        }
        return _filterBuffer;
    }

    private List<LogEntry> ConsumeBatchBuffer()
    {
        var result = _batchBuffer;
        _batchBuffer = new List<LogEntry>(result.Capacity);
        return result;
    }

    private void EnqueuePreparedBatch(PreparedBatch batch)
    {
        _renderQueue.Enqueue(batch);
        if (Interlocked.Exchange(ref _uiDrainScheduled, 1) == 1) return;
        if (!ScintillaControl.IsHandleCreated)
        {
            Interlocked.Exchange(ref _uiDrainScheduled, 0);
            return;
        }
        _dispatchBeginInvoke(DrainRenderQueue);
    }

    private void DrainRenderQueue()
    {
        if (_disposed) return;
        var appliedAny = false;
        _document.BeginBatchUpdate();
        try
        {
            while (true)
            {
                while (_renderQueue.TryDequeue(out var batch))
                {
                    ApplyPreparedBatch(batch);
                    appliedAny = true;
                }
                Interlocked.Exchange(ref _uiDrainScheduled, 0);
                if (_renderQueue.IsEmpty || Interlocked.Exchange(ref _uiDrainScheduled, 1) == 1) break;
            }
        }
        finally { _document.EndBatchUpdate(appliedAny && _autoScroll); }
        if (appliedAny)
        {
            TrimDocumentIfNeeded();
            _document.InvalidateAndRefreshSearch();
        }
    }

    private void ApplyPreparedBatch(PreparedBatch batch)
    {
        try
        {
            if (batch.Epoch != Volatile.Read(ref _clearEpoch)) return;
            if (batch.EntryCount > 0)
            {
                _document.ApplyPreparedBatch(batch);
                _metrics.RecordRendered(batch.EntryCount);
            }
        }
        finally
        {
            batch.ReleaseEntryBuffers();
            batch.ReturnBuffers();
        }
    }

    private void TrimDocumentIfNeeded()
    {
        var lineCount = _document.GetLineCount();
        var overflow = lineCount - _options.MaxLines;
        if (overflow <= 0) return;
        _document.TrimHeadLines(overflow);
    }

    private void DrainAndDiscardRenderQueue()
    {
        while (_renderQueue.TryDequeue(out var batch))
        {
            batch.ReleaseEntryBuffers();
            batch.ReturnBuffers();
        }
    }

    private void Dispatch(Action action) => _dispatchInvoke(action);

    private Task<SearchMatch> DispatchSearchAsync(Func<SearchMatch> action, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<SearchMatch>(ct);
        var tcs = new TaskCompletionSource<SearchMatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.CanBeCanceled ? ct.Register(() => tcs.TrySetCanceled(ct)) : default;
        var capturedRegistration = registration;
        try
        {
            _dispatchBeginInvoke(() =>
            {
                try
                {
                    if (_disposed) { tcs.TrySetResult(SearchMatch.NotFound); return; }
                    tcs.TrySetResult(action());
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
                finally { capturedRegistration.Dispose(); }
            });
        }
        catch (Exception ex)
        {
            registration.Dispose();
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }

    private void RefreshStyles()
    {
        if (_disposed) return;
        _dispatchInvoke(() => _document.ApplyThemeStyles(_renderStrategy, _currentTheme));
    }

    private void ApplyInitialStylesDeferred()
    {
        if (ScintillaControl.IsHandleCreated)
            _document.ApplyThemeStyles(_renderStrategy, _currentTheme);
        else
            ScintillaControl.HandleCreated += OnHandleCreatedApplyStyles;
    }

    private void OnHandleCreatedApplyStyles(object? sender, EventArgs e)
    {
        ScintillaControl.HandleCreated -= OnHandleCreatedApplyStyles;
        _document.ApplyThemeStyles(_renderStrategy, _currentTheme);
        if (Interlocked.Exchange(ref _uiDrainScheduled, 1) == 0)
            _dispatchBeginInvoke(DrainRenderQueue);
    }

    private void ApplyNativeThemeToControl()
    {
        if (ScintillaControl.IsHandleCreated) ApplyNativeTheme();
        else ScintillaControl.HandleCreated += OnHandleCreatedApplyTheme;
    }

    private void OnHandleCreatedApplyTheme(object? sender, EventArgs e)
    {
        ScintillaControl.HandleCreated -= OnHandleCreatedApplyTheme;
        ApplyNativeTheme();
    }

    private void ApplyNativeTheme()
    {
        if (_disposed) return;
        _dispatchInvoke(() =>
        {
            if (!ScintillaControl.IsHandleCreated) return;
            NativeWindowsHelper.ApplyScrollbarTheme(ScintillaControl.Handle, _currentTheme.IsDark);
            NativeWindowsHelper.ApplyContextMenuTheme(_currentTheme.IsDark);
        });
    }

    private void ThrowIfDisposed()
    {
#if NET8_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
#endif
    }

    private static void ReleaseBatchEntries(IReadOnlyList<LogEntry> entries)
    {
#if NET8_0_OR_GREATER
        if (entries is List<LogEntry> list)
        {
            var span = CollectionsMarshal.AsSpan(list);
            foreach (var t in span) t.ReleaseBuffer();
            return;
        }
#endif
        foreach (var t in entries) t.ReleaseBuffer();
    }
}
