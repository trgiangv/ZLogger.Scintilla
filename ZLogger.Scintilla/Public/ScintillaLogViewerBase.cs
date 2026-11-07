using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Controls;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;
using ZLogger.Scintilla.Rendering;
using ZLogger.Scintilla.Services;
// ReSharper disable ForCanBeConvertedToForeach
// Fields read outside _pumpGate are thread-safe on their own: the channel, a fixed setting, or Interlocked flags.
// ReSharper disable InconsistentlySynchronizedField
#if NET9_0_OR_GREATER
using Gate = System.Threading.Lock;
#else
using Gate = System.Object;
#endif
namespace ZLogger.Scintilla.Public;

/// <summary>
/// Log viewer: producers post to a bounded channel, one pump prepares batches, the UI thread applies them.
/// <para>
/// <b>Threads.</b> The channel reader, the history, the preparer and the replay all belong to the pump. Everything
/// that touches the channel, the history or the replay holds <c>_pumpGate</c>; preparing a batch does not,
/// because it runs the application's colorizer and linkifier. A filter change does not replay on its own thread:
/// it asks the pump to, so replay and ingest are ordered and a line is either in the replay or arrives after it.
/// </para>
/// <para>
/// <b>Epoch.</b> Every batch carries the document generation it was prepared for. Clearing or refiltering
/// starts a new generation, and the UI discards a batch from an older one.
/// </para>
/// <para>
/// <b>Backlog.</b> The pump stops reading once <see cref="LogConstants.MaxQueuedBatches"/> batches are waiting
/// for the UI, so the channel's capacity and drop policy limit the whole pipeline.
/// </para>
/// </summary>
public abstract class ScintillaLogViewerBase : IDisposable
{
    private readonly ScintillaDocument _document;
    private readonly Action _drainAction;
    private readonly Func<IZLoggerEntry, LogEntry?> _materialize;
    private readonly LogRenderer _renderStrategy;
    private readonly BatchPreparer _batchPreparer;
    private readonly ScintillaOptions _options;
    private readonly LogIngestPump _ingestPump;
    private readonly LogIngestMetrics _metrics = new();
    private readonly LogHistoryBuffer _history;
    private readonly ConcurrentQueue<PreparedBatch> _renderQueue = new();
    private readonly List<LogEntry> _batchBuffer;
    private readonly List<LogEntry> _filterBuffer;
    private readonly Gate _stateGate = new();
    private readonly Gate _pumpGate = new();
    private readonly SemaphoreSlim _pumpWake = new(0, 1);
    private ScintillaLogProcessor? _processor;
    private volatile CultureInfo? _pumpCulture;
    private List<Action>? _disposeActions;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private Task? _stoppingPump;
    private long _documentEpoch;
    private long _clearGeneration;
    private int _replayRequested;
    private IEnumerator<List<LogEntry>>? _replayChunks;
    private long _replayEpoch;
    private int _drainScheduled;
    private int _drainYielding;
    private int _pumpParked;
    private int _pumpIdle;
    private int _trimPending;
    private int _minimumFilterLevel = (int)LogLevel.Trace;
    private IScintillaTheme _currentTheme;
    private volatile bool _disposed;

    public ScintillaNET.Scintilla ScintillaControl { get; }

    internal bool IncludeScopes => _options.IncludeScopes;

    internal SearchCoordinator Search { get; }

    internal LogFindBar FindBar { get; }

    protected ScintillaLogViewerBase(Action<ScintillaOptions>? configure)
    {
        var resolved = new ScintillaOptions();
        configure?.Invoke(resolved);
        _options = resolved;
        _currentTheme = resolved.Theme;

        var control = new LogScintillaControl
        {
            Dock = DockStyle.Fill,
            BorderStyle = ScintillaNET.BorderStyle.None
        };
        ScintillaControl = control;
        control.Painted += OnControlPainted;


        _drainAction = DrainRenderQueue;

        _document = new ScintillaDocument(control, resolved, _currentTheme);
        _renderStrategy = new LogRenderer(resolved.FontFamily, resolved.FontSize, resolved);
        _batchPreparer = new BatchPreparer(_renderStrategy, ReportError);
        _batchBuffer = new List<LogEntry>(resolved.MaxBatchSize);
        _filterBuffer = new List<LogEntry>(resolved.MaxBatchSize);
        _ingestPump = new LogIngestPump(resolved, OnEntryDropped);
        _materialize = MaterializeDeferred;

        var maxHistory = resolved.DisableHistory ? 0 : Math.Max(0, resolved.MaxHistoryEntries);
        _history = new LogHistoryBuffer(_metrics, maxHistory);
        Search = new SearchCoordinator(_document, Dispatch);
        FindBar = new LogFindBar(control, Search, _document, _currentTheme);

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
            var token = _cts.Token;
            var previous = _stoppingPump;
            _stoppingPump = null;
            _pumpTask = Task.Run(() => PumpAsync(previous, token), token);
        }
    }

    /// <summary>
    /// Stops the pump. Called off the UI thread, it returns once the pump has finished. On the UI thread it returns
    /// at once, because a colorizer or linkifier on the pump may be waiting for that thread; the pump then stops
    /// after its current step, and <see cref="Start"/> lets it finish before another one runs.
    /// </summary>
    public void Stop()
    {
        var onUiThread = !ScintillaControl.InvokeRequired;
        CancellationTokenSource? cts;
        Task? pumpTask;
        lock (_stateGate)
        {
            cts = _cts;
            pumpTask = _pumpTask;
            _cts = null;
            _pumpTask = null;
            if (onUiThread && pumpTask is { IsCompleted: false })
                _stoppingPump = pumpTask;
        }
        if (cts == null) return;
        cts.Cancel();
        if (onUiThread && pumpTask is { IsCompleted: false })
        {
            pumpTask.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), cts,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            try
            {
                pumpTask?.GetAwaiter().GetResult();
            }
            finally
            {
                cts.Dispose();
            }
        }

        // The pump is what ends a pause for a window that does not paint.
        Volatile.Write(ref _drainYielding, 0);
        if (!_renderQueue.IsEmpty)
            ScheduleDrain();
        FinishPendingTrim();
    }

    public void Clear()
    {
        ThrowIfDisposed();
        lock (_pumpGate)
        {
            var epoch = Interlocked.Increment(ref _documentEpoch);
            _clearGeneration++;
            CancelReplay();
            _history.Clear();
            _metrics.DecrementBacklog(_ingestPump.Drain(DiscardPending));
            DiscardRenderQueue();

            // Called off the UI thread, the document is cleared in queue order: waiting here for the UI thread
            // could deadlock, and clearing later could erase lines logged after this call.
            if (ScintillaControl.InvokeRequired)
                _renderQueue.Enqueue(PreparedBatch.ForAction(DocumentAction.Clear, epoch));
            else
                _document.Clear();
        }

        ScheduleDrain();
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
        if (_renderStrategy.IsPrettyJsonEnabled == enabled) return;
        _renderStrategy.SetPrettyJson(enabled);
        RequestReplay();
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
        processor.SetPrettyJson(_renderStrategy.IsPrettyJsonEnabled);
        processor.SetCaptureAllParameters(_options.Linkifier is not null);
        processor.SetDeferredSink(TryPostDeferred, _pumpCulture);
    }

    /// <summary>
    /// Shows entries at or above <paramref name="minimumLevel"/>. With history disabled, there is nothing to
    /// rebuild the document from, so the filter applies to new entries only and existing lines stay.
    /// </summary>
    public void SetFilter(LogLevel minimumLevel)
    {
        ThrowIfDisposed();
        var clamped = ClampFilterLevel(minimumLevel);
        if ((LogLevel)Volatile.Read(ref _minimumFilterLevel) == clamped) return;
        Interlocked.Exchange(ref _minimumFilterLevel, (int)clamped);
        RequestReplay();
    }

    public void HighlightSearch(string pattern, bool useRegex = false) { ThrowIfDisposed(); Search.HighlightSearch(pattern, useRegex); }
    public void ClearSearch() { ThrowIfDisposed(); Search.ClearSearch(); }
    public SearchPosition FindNext() { ThrowIfDisposed(); return Search.FindNext(); }
    public Task<SearchPosition> FindNextAsync(CancellationToken ct = default) { ThrowIfDisposed(); return Search.FindNextAsync(ct); }
    public SearchPosition FindPrevious() { ThrowIfDisposed(); return Search.FindPrevious(); }
    public Task<SearchPosition> FindPreviousAsync(CancellationToken ct = default) { ThrowIfDisposed(); return Search.FindPreviousAsync(ct); }
    public Task<int> CountMatchesAsync(CancellationToken ct = default) { ThrowIfDisposed(); return Search.CountMatchesAsync(ct); }

    public long RenderedMessages => _metrics.RenderedMessages;
    public long DroppedMessages => _metrics.DroppedMessages;
    public long PendingEntryCount => _metrics.PendingEntryCount;
    public long HistoryEntries => _metrics.HistoryEntries;

    internal bool TryPost(LogEntry entry)
    {
        if (_disposed)
            return false;

        _metrics.RecordAccepted();
        if (!_ingestPump.TryPost(entry))
        {
            OnEntryDropped(new PendingEntry(entry));
            return false;
        }

        WakeIfIdle();
        return true;
    }

    /// <summary>
    /// Takes ownership of <paramref name="entry"/>, which goes back to ZLogger once formatted or dropped, unless
    /// the backlog is already large. ZLogger's pools never shrink, so every entry waiting at once would stay
    /// in memory for good. Past the limit the caller formats, which also spreads a burst over its threads.
    /// </summary>
    private bool TryPostDeferred(IZLoggerEntry entry)
    {
        if (_metrics.PendingEntryCount >= LogConstants.MaxDeferredBacklog)
            return false;

        if (_disposed)
        {
            entry.Return();
            return true;
        }

        _metrics.RecordAccepted();
        if (!_ingestPump.TryPost(entry))
            OnEntryDropped(new PendingEntry(entry));
        else
            WakeIfIdle();
        return true;
    }

    private LogEntry? MaterializeDeferred(IZLoggerEntry entry)
    {
        if (!WouldLeaveHistoryAtOnce())
            return _processor!.Materialize(entry);

        entry.Return();
        return null;
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
        ScintillaControl.Painted -= OnControlPainted;
        Stop();
        lock (_pumpGate)
        {
            CancelReplay();
            _metrics.DecrementBacklog(_ingestPump.Drain(DiscardPending));
            _history.Clear();
            DiscardRenderQueue();
            _ingestPump.Complete();
        }
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

    private async Task PumpAsync(Task? previous, CancellationToken cancellationToken)
    {
        // A pump that Stop() did not wait for finishes first, so the pipeline never has two owners.
        if (previous is not null)
            await previous.ConfigureAwait(false);

        try
        {
            // Deferred messages are formatted under this culture, so only callers using the same one may defer.
            _pumpCulture = CultureInfo.CurrentCulture;
            _processor?.SetDeferredSink(TryPostDeferred, _pumpCulture);

            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitForPumpWorkAsync(cancellationToken).ConfigureAwait(false);
                FinishPumpPass(RunPumpSteps(cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called.
        }
    }

    private async Task WaitForPumpWorkAsync(CancellationToken cancellationToken)
    {
        await _pumpWake.WaitAsync(_options.FlushIntervalMs, cancellationToken).ConfigureAwait(false);

        // The UI thread paused after a long drain. The next paint normally ends the pause; this is for a
        // window that does not paint, such as a minimized one.
        if (Volatile.Read(ref _drainYielding) != 0)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _drainYielding, 0);
        }
    }

    /// <summary>Runs pump steps until there is nothing left to do. Returns whether any step did work.</summary>
    private bool RunPumpSteps(CancellationToken cancellationToken)
    {
        var worked = false;
        while (!cancellationToken.IsCancellationRequested && PumpStep())
            worked = true;
        return worked;
    }

    private void FinishPumpPass(bool worked)
    {
        if (!_renderQueue.IsEmpty)
            ScheduleDrain();

        MarkIdleIf(!worked);
        if (!worked)
            FinishPendingTrim();
    }

    /// <summary>
    /// After a quiet interval, the next message is shown at once instead of waiting for the next flush.
    /// Only messages close together are held back to be batched, which is when batching helps.
    /// </summary>
    private void MarkIdleIf(bool idle)
    {
        Volatile.Write(ref _pumpIdle, idle ? 1 : 0);
        if (idle && _metrics.PendingEntryCount > 0)
            WakeIfIdle();
    }

    private void WakeIfIdle()
    {
        if (Volatile.Read(ref _pumpIdle) != 0 && Interlocked.Exchange(ref _pumpIdle, 0) != 0)
            WakePump();
    }

    /// <summary>Does one unit of work. Returns false when there is nothing to do or the UI must catch up first.</summary>
    private bool PumpStep()
    {
        if (_renderQueue.Count >= LogConstants.MaxQueuedBatches)
        {
            Volatile.Write(ref _pumpParked, 1);
            ScheduleDrain();
            return false;
        }

        if (_disposed)
            return false;

        try
        {
            return Volatile.Read(ref _replayRequested) != 0 || Volatile.Read(ref _replayChunks) is not null
                ? ReplayStep()
                : IngestStep();
        }
        catch (Exception ex)
        {
            // The entries involved are already out of the channel, so this cannot repeat on the same input.
            ReportError(ex);
            return true;
        }
    }

    /// <summary>
    /// Reads and records entries under the lock, and prepares them outside it: preparing runs the colorizer and
    /// linkifier, which may wait for the UI thread while the UI thread waits for the lock in <see cref="Clear"/>.
    /// </summary>
    private bool IngestStep()
    {
        long epoch;
        long clearGeneration;
        lock (_pumpGate)
        {
            if (_disposed || _ingestPump.TryReadBatch(_batchBuffer, _materialize, _metrics) == 0)
                return false;

            epoch = Volatile.Read(ref _documentEpoch);
            clearGeneration = _clearGeneration;
        }

        try
        {
            var visible = FilterBatch((LogLevel)Volatile.Read(ref _minimumFilterLevel));
            if (visible.Count > 0 && !WouldBeTrimmedAtOnce())
                EnqueuePreparedBatch(_batchPreparer.Prepare(visible, epoch));
        }
        finally
        {
            lock (_pumpGate)
            {
                // A Clear() while these were being prepared removed them along with everything before it.
                if (_history.IsEnabled && clearGeneration == _clearGeneration)
                {
                    _history.EnqueueBatch(_batchBuffer);
                    _history.TrimIfNeeded();
                }
            }

            _batchBuffer.Clear();
        }

        return true;
    }

    /// <summary>
    /// True when the lines still waiting in the channel will fill the document on their own, so that
    /// these would be trimmed before anyone could see them. Waiting lines only count if they are certain
    /// to arrive: a filter can hide them, and a drop policy that removes queued entries can lose them.
    /// </summary>
    private bool WouldBeTrimmedAtOnce() =>
        _metrics.PendingEntryCount >= _options.MaxLines
        && (LogLevel)Volatile.Read(ref _minimumFilterLevel) <= LogLevel.Trace
        && _options.DropPolicy is System.Threading.Channels.BoundedChannelFullMode.DropWrite or System.Threading.Channels.BoundedChannelFullMode.Wait;

    /// <summary>The same for the history, so that formatting the entry would serve no purpose at all.</summary>
    private bool WouldLeaveHistoryAtOnce() =>
        WouldBeTrimmedAtOnce() && _metrics.PendingEntryCount >= _options.MaxHistoryEntries;

    private List<LogEntry> FilterBatch(LogLevel minimumLevel)
    {
        if (minimumLevel <= LogLevel.Trace)
            return _batchBuffer;

        _filterBuffer.Clear();
        for (var i = 0; i < _batchBuffer.Count; i++)
        {
            if (_batchBuffer[i].Level >= minimumLevel)
                _filterBuffer.Add(_batchBuffer[i]);
        }
        return _filterBuffer;
    }

    private void RequestReplay()
    {
        if (!_history.IsEnabled)
            return;

        // The epoch first: a pump that sees the request must also see the epoch it belongs to.
        Interlocked.Increment(ref _documentEpoch);
        Volatile.Write(ref _replayRequested, 1);
        WakePump();
    }

    /// <summary>
    /// Replays history one chunk at a time, so that the render queue's limit and the UI's drain budget apply.
    /// Ingest waits until it finishes, which keeps every line in order and in the document exactly once.
    /// </summary>
    private bool ReplayStep()
    {
        IEnumerator<List<LogEntry>> chunks;
        List<LogEntry> chunk;
        long epoch;
        lock (_pumpGate)
        {
            if (_disposed)
                return false;

            if (Interlocked.Exchange(ref _replayRequested, 0) != 0)
            {
                _replayChunks?.Dispose();
                _replayEpoch = Volatile.Read(ref _documentEpoch);
                var level = (LogLevel)Volatile.Read(ref _minimumFilterLevel);
                _replayChunks = _history.CaptureChunkedSnapshot(level, _options.MaxBatchSize).GetEnumerator();
                EnqueuePreparedBatch(PreparedBatch.ForAction(DocumentAction.ResetForReplay, _replayEpoch));
                return true;
            }

            // Cleared since PumpStep looked.
            if (_replayChunks is null)
                return true;

            if (!_replayChunks.MoveNext())
            {
                CancelReplay();
                return true;
            }

            chunks = _replayChunks;
            // The snapshot iterator never yields null; net48's IEnumerator<T>.Current is not annotated.
            // ReSharper disable AssignNullToNotNullAttribute
            chunk = chunks.Current;
            epoch = _replayEpoch;
        }

        // Outside the lock for the same reason as in IngestStep. The chunk is a list of its own, so a Clear() does
        // not change it.
        var prepared = _batchPreparer.Prepare(chunk, epoch);
        // ReSharper restore AssignNullToNotNullAttribute
        lock (_pumpGate)
        {
            if (ReferenceEquals(chunks, _replayChunks))
                EnqueuePreparedBatch(prepared);
            else
                prepared.ReturnBuffers();
        }

        return true;
    }

    private void CancelReplay()
    {
        Volatile.Write(ref _replayRequested, 0);
        _replayChunks?.Dispose();
        _replayChunks = null;
    }

    private void OnEntryDropped(PendingEntry entry)
    {
        _metrics.RecordDrop();
        DiscardPending(entry);
    }

    private static void DiscardPending(PendingEntry entry)
    {
        entry.Deferred?.Return();
    }

    private void WakePump()
    {
        try
        {
            if (_pumpWake.CurrentCount == 0)
                _pumpWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another thread woke it first, which is all this wanted.
        }
    }

    private void EnqueuePreparedBatch(in PreparedBatch batch)
    {
        // A pump that Stop() did not wait for can finish a batch after Dispose() emptied the queue.
        if (_disposed)
        {
            batch.ReturnBuffers();
            return;
        }

        _renderQueue.Enqueue(batch);
        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (Volatile.Read(ref _drainYielding) != 0)
            return;
        if (Interlocked.Exchange(ref _drainScheduled, 1) != 0)
            return;

        try
        {
            if (_disposed || ScintillaControl.IsDisposed || !ScintillaControl.IsHandleCreated)
                Volatile.Write(ref _drainScheduled, 0);
            else
                ScintillaControl.BeginInvoke(_drainAction);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The handle went away. The pump schedules again on its next pass, and so does OnHandleCreated.
            Volatile.Write(ref _drainScheduled, 0);
        }
    }

    private void DrainRenderQueue()
    {
        // Cleared before any work can throw, so that a failure cannot leave rendering stopped for good.
        Volatile.Write(ref _drainScheduled, 0);
        if (_disposed)
            return;

        try
        {
            ApplyQueuedBatches();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }

        // A drain posted right away would run ahead of paint and input, because Windows delivers posted
        // messages first, and the window would freeze for as long as logs kept coming. So the next one
        // waits for the next paint, which comes after any pending input.
        var more = !_renderQueue.IsEmpty;
        if (more && _pumpTask is not null)
        {
            Volatile.Write(ref _drainYielding, 1);
            WakePump();
        }
        else if (more)
        {
            ScheduleDrain();
        }
        else if (Interlocked.Exchange(ref _pumpParked, 0) != 0)
        {
            WakePump();
        }
    }

    private void OnControlPainted(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _drainYielding, 0) != 0)
            ScheduleDrain();
    }

    private void ApplyQueuedBatches()
    {
        if (_renderQueue.IsEmpty)
            return;

        var deadline = Stopwatch.GetTimestamp() + LogConstants.DrainBudgetMs * Stopwatch.Frequency / 1000;
        var changed = false;
        _document.BeginBatchUpdate();
        try
        {
            while (_renderQueue.TryDequeue(out var batch))
            {
                changed |= ApplyBatch(batch);
                if (Stopwatch.GetTimestamp() >= deadline)
                    break;
            }

            if (changed)
                TrimDocumentIfNeeded();
        }
        finally
        {
            _document.EndBatchUpdate(changed);
        }

        if (changed)
            _document.RefreshSearch();
    }

    private bool ApplyBatch(in PreparedBatch batch)
    {
        try
        {
            if (batch.Epoch != Volatile.Read(ref _documentEpoch))
                return false;

            switch (batch.Action)
            {
                case DocumentAction.ResetForReplay:
                    _document.ResetForReplay();
                    return true;
                case DocumentAction.Clear:
                    _document.Clear();
                    return true;
            }

            if (batch.EntryCount <= 0)
                return false;

            _document.ApplyPreparedBatch(batch);
            _metrics.RecordRendered(batch.EntryCount);
            return true;
        }
        finally
        {
            batch.ReturnBuffers();
        }
    }

    /// <summary>
    /// Under load, trims in steps of a tenth of the document. Scintilla remembers the lines waiting for word wrap
    /// as one range, so a trim at the start plus an append at the end queues the whole document to be wrapped
    /// again, and doing that on every drain keeps the UI thread busy for as long as logs keep coming.
    /// The document returns to exactly <see cref="ScintillaOptions.MaxLines"/> as soon as the logs pause.
    /// </summary>
    private void TrimDocumentIfNeeded()
    {
        var overflow = GetOverflowLines();
        if (overflow <= 0)
            return;

        if (overflow < Math.Max(LogConstants.MinTrimStepLines, _options.MaxLines / 10))
        {
            Volatile.Write(ref _trimPending, 1);
            return;
        }

        Volatile.Write(ref _trimPending, 0);
        _document.TrimHeadLines(overflow);
    }

    private void TrimDocumentExactly()
    {
        if (_disposed)
            return;

        var overflow = GetOverflowLines();
        if (overflow <= 0)
            return;

        _document.TrimHeadLines(overflow);
        _document.RefreshSearch();
    }

    /// <summary>Scintilla counts the empty line after the final line break, which holds no message.</summary>
    private int GetOverflowLines() => _document.GetLineCount() - 1 - _options.MaxLines;

    private void FinishPendingTrim()
    {
        if (Interlocked.Exchange(ref _trimPending, 0) == 0)
            return;

        try
        {
            if (!_disposed && ScintillaControl.IsHandleCreated)
                ScintillaControl.BeginInvoke(TrimDocumentExactly);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The handle went away, and the document with it.
        }
    }

    private void DiscardRenderQueue()
    {
        while (_renderQueue.TryDequeue(out var batch))
            batch.ReturnBuffers();
    }

    private void ReportError(Exception ex)
    {
        try
        {
            _options.EnrichmentErrorSink?.Invoke(ex);
        }
        catch
        {
            // A broken sink must not stop the pipeline it reports on.
        }
    }

    private void Dispatch(Action action)
    {
        if (ScintillaControl.IsDisposed)
            return;

        if (ScintillaControl.InvokeRequired)
            ScintillaControl.Invoke(action);
        else
            action();
    }

    private void RefreshStyles()
    {
        if (_disposed) return;
        Dispatch(() =>
        {
            _document.ApplyThemeStyles(_renderStrategy, _currentTheme);
            FindBar.ApplyTheme(_currentTheme);
        });
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
        ScheduleDrain();
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
        Dispatch(() =>
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
}
