using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
// ReSharper disable ForCanBeConvertedToForeach
// Fields read outside _pipelineGate are thread-safe on their own: the channel, a fixed setting, or Interlocked flags.
// ReSharper disable InconsistentlySynchronizedField
namespace ZLogger.Scintilla.Pipeline;

/// <summary>
/// The background half of the pipeline: producers post to a bounded channel, and one pump reads it, keeps the
/// history, prepares batches and hands them to the <see cref="RenderQueue"/>.
/// <para>
/// <b>Threads.</b> The channel reader, the history, the preparer and the replay all belong to the pump. Everything
/// that touches the channel, the history or the replay holds <c>_pipelineGate</c>; preparing a batch does not,
/// because it runs the application's colorizer and linkifier. A filter change does not replay on its own thread:
/// it asks the pump to, so replay and ingest are ordered and a line is either in the replay or arrives after it.
/// </para>
/// <para>
/// <b>Backlog.</b> The pump stops reading while the render queue is full, so the channel's capacity and drop
/// policy limit the whole pipeline.
/// </para>
/// </summary>
internal sealed class BatchPump
{
    private readonly ScintillaOptions _options;
    private readonly LogIngestMetrics _metrics;
    private readonly RenderQueue _renderQueue;
    private readonly BatchPreparer _preparer;
    private readonly Action<Exception> _reportError;
    private readonly LogIngestChannel _channel;
    private readonly LogHistoryBuffer _history;
    private readonly List<LogEntry> _batchBuffer;
    private readonly List<LogEntry> _filterBuffer;
    private readonly Func<IZLoggerEntry, LogEntry?> _materialize;
    private readonly object _lifecycleGate = new();
    private readonly object _pipelineGate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private ScintillaLogProcessor? _processor;
    private volatile CultureInfo? _culture;
    private volatile PumpRun? _run;
    private Task? _previousRun;
    private long _clearCount;
    private int _replayRequested;
    private IEnumerator<List<LogEntry>>? _replayChunks;
    private long _replayEpoch;
    private int _idle;
    private int _minimumLevel = (int)LogLevel.Trace;
    private volatile bool _closed;

    public BatchPump(
        ScintillaOptions options,
        LogIngestMetrics metrics,
        RenderQueue renderQueue,
        BatchPreparer preparer,
        Action<Exception> reportError)
    {
        _options = options;
        _metrics = metrics;
        _renderQueue = renderQueue;
        _preparer = preparer;
        _reportError = reportError;
        _channel = new LogIngestChannel(options, OnEntryDropped);
        _history = new LogHistoryBuffer(metrics, options.DisableHistory ? 0 : Math.Max(0, options.MaxHistoryEntries));
        _batchBuffer = new List<LogEntry>(options.MaxBatchSize);
        _filterBuffer = new List<LogEntry>(options.MaxBatchSize);
        _materialize = MaterializeDeferred;
        renderQueue.AttachPump(this);
    }

    public bool IsRunning => _run is not null;

    private LogLevel MinimumLevel => (LogLevel)Volatile.Read(ref _minimumLevel);

    private bool ReplayInProgress => Volatile.Read(ref _replayRequested) != 0 || Volatile.Read(ref _replayChunks) is not null;

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_run is not null) return;
            var cancellation = new CancellationTokenSource();
            var previous = _previousRun;
            _previousRun = null;
            _run = new PumpRun(Task.Run(() => RunAsync(previous, cancellation.Token), cancellation.Token), cancellation);
        }
    }

    /// <summary>
    /// Stops the pump, and returns whether one was running. With <paramref name="waitForPump"/> false it returns
    /// at once, because a colorizer or linkifier on the pump may be waiting for the calling thread; the pump then
    /// stops after its current step, and <see cref="Start"/> lets it finish before another one runs.
    /// </summary>
    public bool Stop(bool waitForPump)
    {
        var run = TakeRun(waitForPump);
        if (run is null)
            return false;

        run.Cancellation.Cancel();
        if (waitForPump || run.Task.IsCompleted)
            run.WaitAndDispose();
        else
            run.DisposeWhenDone();
        return true;
    }

    /// <summary>Stops the pump and lets go of everything waiting. Nothing is accepted afterwards.</summary>
    public void Close(bool waitForPump)
    {
        _closed = true;
        Stop(waitForPump);
        lock (_pipelineGate)
        {
            CancelReplay();
            _metrics.DecrementBacklog(_channel.DiscardAll(DiscardPending));
            _history.Clear();
            _renderQueue.DiscardAll();
            _channel.Complete();
        }
    }

    /// <summary>Removes every waiting and recorded entry, and the document's lines with them.</summary>
    public void Clear()
    {
        lock (_pipelineGate)
        {
            var epoch = _renderQueue.AdvanceEpoch();
            _clearCount++;
            CancelReplay();
            _history.Clear();
            _metrics.DecrementBacklog(_channel.DiscardAll(DiscardPending));
            _renderQueue.DiscardAll();
            _renderQueue.ClearDocument(epoch);
        }

        _renderQueue.Schedule();
    }

    /// <summary>
    /// Shows entries at or above <paramref name="minimumLevel"/>. With history disabled, there is nothing to
    /// rebuild the document from, so the filter applies to new entries only and existing lines stay.
    /// </summary>
    public void SetFilter(LogLevel minimumLevel)
    {
        if (Interlocked.Exchange(ref _minimumLevel, (int)minimumLevel) != (int)minimumLevel)
            RequestReplay();
    }

    /// <summary>Rebuilds the document from history, as when the filter or the rendering changes.</summary>
    public void RequestReplay()
    {
        if (!_history.IsEnabled)
            return;

        // The epoch first: a pump that sees the request must also see the epoch it belongs to.
        _renderQueue.AdvanceEpoch();
        Volatile.Write(ref _replayRequested, 1);
        Wake();
    }

    public void AttachProcessor(ScintillaLogProcessor processor)
    {
        _processor = processor;
        processor.SetDeferredSink(TryPostDeferred, _culture);
    }

    public bool TryPost(LogEntry entry)
    {
        if (_closed)
            return false;

        _metrics.RecordAccepted();
        if (!_channel.TryPost(entry))
        {
            OnEntryDropped(new PendingEntry(entry));
            return false;
        }

        WakeIfIdle();
        return true;
    }

    public void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another thread woke it first, which is all this wanted.
        }
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

        if (_closed)
        {
            entry.Return();
            return true;
        }

        _metrics.RecordAccepted();
        if (!_channel.TryPost(entry))
            OnEntryDropped(new PendingEntry(entry));
        else
            WakeIfIdle();
        return true;
    }

    private LogEntry? MaterializeDeferred(IZLoggerEntry entry)
    {
        if (!BacklogFillsHistory())
            return _processor!.Materialize(entry);

        entry.Return();
        return null;
    }

    /// <summary>
    /// Takes the current run. One that is not waited for must finish before the next starts, so the pipeline
    /// never has two owners.
    /// </summary>
    private PumpRun? TakeRun(bool waitForPump)
    {
        lock (_lifecycleGate)
        {
            var run = _run;
            _run = null;
            if (run is not null && !waitForPump && !run.Task.IsCompleted)
                _previousRun = run.Task;
            return run;
        }
    }

    private async Task RunAsync(Task? previous, CancellationToken cancellationToken)
    {
        if (previous is not null)
            await previous.ConfigureAwait(false);

        try
        {
            // Deferred messages are formatted under this culture, so only callers using the same one may defer.
            _culture = CultureInfo.CurrentCulture;
            _processor?.SetDeferredSink(TryPostDeferred, _culture);

            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
                RunPass(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() was called.
        }
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        await _wake.WaitAsync(_options.FlushIntervalMs, cancellationToken).ConfigureAwait(false);

        var pause = PauseForRenderQueue();
        if (pause == 0)
            return;

        await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        _renderQueue.StopAwaitingPaint();
    }

    /// <summary>
    /// How long to wait before ending the render queue's wait for a paint, in milliseconds, or 0 when it is not
    /// waiting. A drain awaiting a paint normally gets one; this is for a window that does not paint, such as a
    /// minimized one. A parked editor is not about to come back, and waiting logs keep waking the pump, so then
    /// the pump waits a whole flush interval.
    /// </summary>
    private int PauseForRenderQueue()
    {
        if (_renderQueue.IsEditorParked)
            return _options.FlushIntervalMs;
        return _renderQueue.IsAwaitingPaint ? 1 : 0;
    }

    /// <summary>Works until there is nothing left to do or the render queue is full.</summary>
    private void RunPass(CancellationToken cancellationToken)
    {
        var worked = false;
        while (!cancellationToken.IsCancellationRequested && TryDoWork())
            worked = true;

        if (_renderQueue.HasPending)
            _renderQueue.Schedule();

        if (worked)
            Volatile.Write(ref _idle, 0);
        else
            GoIdle();
    }

    /// <summary>
    /// After a quiet pass, the next message is shown at once instead of waiting for the next flush. Only messages
    /// close together are held back to be batched, which is when batching helps. A quiet pass is also when the
    /// document is trimmed to exactly its limit.
    /// </summary>
    private void GoIdle()
    {
        Volatile.Write(ref _idle, 1);
        if (_metrics.PendingEntryCount > 0)
            WakeIfIdle();
        _renderQueue.FinishPendingTrim();
    }

    private void WakeIfIdle()
    {
        if (Volatile.Read(ref _idle) != 0 && Interlocked.Exchange(ref _idle, 0) != 0)
            Wake();
    }

    /// <summary>Does one unit of work. Returns false when there is nothing to do or the UI must catch up first.</summary>
    private bool TryDoWork()
    {
        if (_renderQueue.IsFull)
        {
            _renderQueue.BlockPump();
            return false;
        }

        if (_closed)
            return false;

        try
        {
            return ReplayInProgress ? ReplayChunk() : IngestBatch();
        }
        catch (Exception ex)
        {
            // The entries involved are already out of the channel, so this cannot repeat on the same input.
            _reportError(ex);
            return true;
        }
    }

    /// <summary>
    /// Reads and records entries under the lock, and prepares them outside it: preparing runs the colorizer and
    /// linkifier, which may wait for the UI thread while the UI thread waits for the lock in <see cref="Clear"/>.
    /// </summary>
    private bool IngestBatch()
    {
        if (!TryReadBatch(out var epoch, out var clearCount))
            return false;

        try
        {
            var visible = FilterBatch(MinimumLevel);
            if (visible.Count > 0 && !BacklogFillsDocument())
                _renderQueue.Enqueue(_preparer.Prepare(visible, epoch));
        }
        finally
        {
            RecordInHistory(clearCount);
            _batchBuffer.Clear();
        }

        return true;
    }

    private bool TryReadBatch(out long epoch, out long clearCount)
    {
        lock (_pipelineGate)
        {
            epoch = _renderQueue.Epoch;
            clearCount = _clearCount;
            return !_closed && _channel.TryReadBatch(_batchBuffer, _materialize, _metrics) > 0;
        }
    }

    /// <summary>A Clear() while the batch was being prepared removed it along with everything before it.</summary>
    private void RecordInHistory(long clearCount)
    {
        lock (_pipelineGate)
        {
            if (!_history.IsEnabled || clearCount != _clearCount)
                return;

            _history.EnqueueBatch(_batchBuffer);
            _history.TrimIfNeeded();
        }
    }

    /// <summary>
    /// True when the lines still waiting in the channel will fill the document on their own, so that
    /// these would be trimmed before anyone could see them. Waiting lines only count if they are certain
    /// to arrive: a filter can hide them, and a drop policy that removes queued entries can lose them.
    /// </summary>
    private bool BacklogFillsDocument() =>
        _metrics.PendingEntryCount >= _options.MaxLines
        && MinimumLevel <= LogLevel.Trace
        && _options.DropPolicy is BoundedChannelFullMode.DropWrite or BoundedChannelFullMode.Wait;

    /// <summary>The same for the history, so that formatting the entry would serve no purpose at all.</summary>
    private bool BacklogFillsHistory() =>
        BacklogFillsDocument() && _metrics.PendingEntryCount >= _options.MaxHistoryEntries;

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

    /// <summary>
    /// Replays history one chunk at a time, so that the render queue's limit and the UI's drain budget apply.
    /// Ingest waits until it finishes, which keeps every line in order and in the document exactly once.
    /// </summary>
    private bool ReplayChunk()
    {
        IEnumerator<List<LogEntry>>? chunks;
        long epoch;
        lock (_pipelineGate)
        {
            if (_closed)
                return false;

            if (Interlocked.Exchange(ref _replayRequested, 0) != 0)
            {
                BeginReplay();
                return true;
            }

            chunks = MoveToNextChunk();
            epoch = _replayEpoch;
        }

        if (chunks is null)
            return true;

        // Outside the lock for the same reason as in IngestBatch. The chunk is a list of its own, so a Clear()
        // does not change it.
        // The snapshot iterator never yields null; net48's IEnumerator<T>.Current is not annotated.
        // ReSharper disable once AssignNullToNotNullAttribute
        EnqueueIfStillReplaying(chunks, _preparer.Prepare(chunks.Current, epoch));
        return true;
    }

    private void BeginReplay()
    {
        _replayChunks?.Dispose();
        _replayEpoch = _renderQueue.Epoch;
        _replayChunks = _history.CaptureChunkedSnapshot(MinimumLevel, _options.MaxBatchSize).GetEnumerator();
        _renderQueue.Enqueue(PreparedBatch.ForAction(DocumentAction.ResetForReplay, _replayEpoch));
    }

    /// <summary>
    /// Returns the replay positioned on its next chunk, or null when there is none: after the last chunk, which
    /// ends the replay, or when a Clear() cancelled it since <see cref="TryDoWork"/> looked.
    /// </summary>
    private IEnumerator<List<LogEntry>>? MoveToNextChunk()
    {
        if (_replayChunks is null)
            return null;
        if (_replayChunks.MoveNext())
            return _replayChunks;

        CancelReplay();
        return null;
    }

    /// <summary>A Clear() or a newer replay while the chunk was being prepared makes it obsolete.</summary>
    private void EnqueueIfStillReplaying(IEnumerator<List<LogEntry>> chunks, in PreparedBatch prepared)
    {
        lock (_pipelineGate)
        {
            if (ReferenceEquals(chunks, _replayChunks))
                _renderQueue.Enqueue(prepared);
            else
                prepared.ReturnBuffers();
        }
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

    /// <summary>One start of the pump: its task and the source that cancels it.</summary>
    private sealed record PumpRun(Task Task, CancellationTokenSource Cancellation)
    {
        public void WaitAndDispose()
        {
            try
            {
                Task.GetAwaiter().GetResult();
            }
            finally
            {
                Cancellation.Dispose();
            }
        }

        public void DisposeWhenDone() =>
            Task.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), Cancellation,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
