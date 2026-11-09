using System.Collections.Concurrent;
using System.Diagnostics;
using ZLogger.Scintilla.Controls;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
using ZLogger.Scintilla.Services;
namespace ZLogger.Scintilla.Pipeline;

/// <summary>
/// The UI thread's half of the pipeline: prepared batches wait here until a drain on the UI thread applies them
/// to the document and trims it.
/// <para>
/// <b>Pacing.</b> A drain posted right away would run ahead of paint and input, so after a drain that left
/// batches behind, the next one awaits a paint. The pump ends that wait for a window that does not paint.
/// </para>
/// <para>
/// <b>Epoch.</b> Every batch carries the document generation it was prepared for. Clearing or refiltering
/// starts a new generation, and a batch from an older one is discarded.
/// </para>
/// </summary>
internal sealed class RenderQueue
{
    private readonly ConcurrentQueue<PreparedBatch> _batches = new();
    private readonly LogScintillaControl _control;
    private readonly ScintillaDocument _document;
    private readonly ScintillaOptions _options;
    private readonly LogIngestMetrics _metrics;
    private readonly Action<Exception> _reportError;
    private readonly Action _drainAction;
    private readonly Action _trimAction;
    private BatchPump? _pump;
    private long _epoch;
    private int _drainPosted;
    private int _awaitingPaint;
    private int _editorParked;
    private int _pumpBlocked;
    private int _trimPending;
    private volatile bool _closed;

    public RenderQueue(
        LogScintillaControl control,
        ScintillaDocument document,
        ScintillaOptions options,
        LogIngestMetrics metrics,
        Action<Exception> reportError)
    {
        _control = control;
        _document = document;
        _options = options;
        _metrics = metrics;
        _reportError = reportError;
        _drainAction = Drain;
        _trimAction = TrimExactly;
        control.Painted += OnPainted;
    }

    /// <summary>The pump to wake when the queue has room again, or after a drain that left batches behind.</summary>
    public void AttachPump(BatchPump pump) => _pump = pump;

    /// <summary>The current document generation.</summary>
    public long Epoch => Volatile.Read(ref _epoch);

    /// <summary>Starts a new document generation, so that batches prepared for the old one are discarded.</summary>
    public long AdvanceEpoch() => Interlocked.Increment(ref _epoch);

    public int Count => _batches.Count;

    public bool HasPending => !_batches.IsEmpty;

    /// <summary>
    /// True when <see cref="LogConstants.MaxQueuedBatches"/> batches are waiting. The pump then stops reading,
    /// so the channel's capacity and drop policy limit the whole pipeline.
    /// </summary>
    public bool IsFull => _batches.Count >= LogConstants.MaxQueuedBatches;

    /// <summary>True while the next drain waits for a paint.</summary>
    public bool IsAwaitingPaint => Volatile.Read(ref _awaitingPaint) != 0;

    /// <summary>True when the last drain found the editor parked. See <see cref="LogScintillaControl.IsParked"/>.</summary>
    public bool IsEditorParked => Volatile.Read(ref _editorParked) != 0;

    /// <summary>Lets the next drain run without a paint, for a window that does not paint, such as a minimized one.</summary>
    public void StopAwaitingPaint() => Volatile.Write(ref _awaitingPaint, 0);

    /// <summary>The pump stopped reading because the queue is full. The drain wakes it once the queue is empty.</summary>
    public void BlockPump()
    {
        Volatile.Write(ref _pumpBlocked, 1);
        Schedule();
    }

    public void Enqueue(in PreparedBatch batch)
    {
        // A pump that Stop() did not wait for can finish a batch after the viewer closed.
        if (_closed)
        {
            batch.ReturnBuffers();
            return;
        }

        _batches.Enqueue(batch);
        Schedule();
    }

    /// <summary>
    /// Clears the document. On the UI thread it happens now; from any other, in queue order: waiting for the UI
    /// thread could deadlock, and clearing later could erase lines logged after the call.
    /// </summary>
    public void ClearDocument(long epoch)
    {
        if (_control.InvokeRequired)
            _batches.Enqueue(PreparedBatch.ForAction(DocumentAction.Clear, epoch));
        else
            _document.Clear();
    }

    /// <summary>Posts a drain to the UI thread, unless one is already posted or the next one awaits a paint.</summary>
    public void Schedule()
    {
        if (IsAwaitingPaint || Interlocked.Exchange(ref _drainPosted, 1) != 0)
            return;

        // Without a window there is nothing to post to. The pump schedules again on its next pass, and so does
        // the viewer once the window is created.
        if (!TryPost(_drainAction))
            Volatile.Write(ref _drainPosted, 0);
    }

    /// <summary>Once the pump has stopped, nothing else would end the wait for a window that does not paint.</summary>
    public void OnPumpStopped()
    {
        StopAwaitingPaint();
        if (HasPending)
            Schedule();
        FinishPendingTrim();
    }

    /// <summary>Trims the document to exactly <see cref="ScintillaOptions.MaxLines"/>, once the logs pause.</summary>
    public void FinishPendingTrim()
    {
        if (Interlocked.Exchange(ref _trimPending, 0) != 0)
            TryPost(_trimAction);
    }

    public void DiscardAll()
    {
        while (_batches.TryDequeue(out var batch))
            batch.ReturnBuffers();
    }

    /// <summary>Stops drawing. Batches enqueued from now on are discarded.</summary>
    public void Close()
    {
        _closed = true;
        _control.Painted -= OnPainted;
    }

    private bool TryPost(Action action)
    {
        if (_closed || _control.IsDisposed || !_control.IsHandleCreated)
            return false;

        try
        {
            _control.BeginInvoke(action);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The window went away between the check and the post.
            return false;
        }
    }

    private void OnPainted(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _awaitingPaint, 0) != 0)
            Schedule();
    }

    private void Drain()
    {
        // Cleared before any work can throw, so that a failure cannot leave rendering stopped for good.
        Volatile.Write(ref _drainPosted, 0);
        if (_closed || HoldWhileParked())
            return;

        try
        {
            ApplyWithinBudget();
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }

        ScheduleNext();
    }

    /// <summary>
    /// An edit while the editor is parked would put ScintillaNET's line index out of step with the document.
    /// The batches wait: the pump tries again on its next pass, and the first paint once the host shows the
    /// editor again ends the wait.
    /// </summary>
    private bool HoldWhileParked()
    {
        var parked = _control.IsParked;
        Volatile.Write(ref _editorParked, parked ? 1 : 0);
        if (parked)
            Volatile.Write(ref _awaitingPaint, 1);
        return parked;
    }

    /// <summary>
    /// A drain posted right away would run ahead of paint and input, because Windows delivers posted messages
    /// first, and the window would freeze for as long as logs kept coming. So the next one awaits the next paint,
    /// which comes after any pending input. Without a pump to end that wait, it is posted at once.
    /// </summary>
    private void ScheduleNext()
    {
        if (!HasPending)
        {
            if (Interlocked.Exchange(ref _pumpBlocked, 0) != 0)
                _pump?.Wake();
            return;
        }

        if (_pump is { IsRunning: true })
        {
            Volatile.Write(ref _awaitingPaint, 1);
            _pump.Wake();
        }
        else
        {
            Schedule();
        }
    }

    private void ApplyWithinBudget()
    {
        if (_batches.IsEmpty)
            return;

        var changed = false;
        _document.BeginBatchUpdate();
        try
        {
            ApplyBatches(ref changed);
            if (changed)
                TrimInSteps();
        }
        finally
        {
            _document.EndBatchUpdate(changed);
        }

        if (changed)
            _document.RefreshSearch();
    }

    /// <summary>
    /// Applies batches for up to <see cref="LogConstants.DrainBudgetMs"/>. <paramref name="changed"/> is set as
    /// each batch lands, so that the batches applied before a failure still get scrolled to.
    /// </summary>
    private void ApplyBatches(ref bool changed)
    {
        var deadline = Stopwatch.GetTimestamp() + LogConstants.DrainBudgetMs * Stopwatch.Frequency / 1000;
        while (_batches.TryDequeue(out var batch))
        {
            changed |= ApplyBatch(batch);
            if (Stopwatch.GetTimestamp() >= deadline)
                return;
        }
    }

    private bool ApplyBatch(in PreparedBatch batch)
    {
        try
        {
            if (batch.Epoch != Epoch)
                return false;

            switch (batch.Action)
            {
                case DocumentAction.Append:
                    return Append(batch);
                case DocumentAction.ResetForReplay:
                    _document.ResetForReplay();
                    return true;
                case DocumentAction.Clear:
                    _document.Clear();
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(batch), batch.Action, "Unknown document action.");
            }
        }
        finally
        {
            batch.ReturnBuffers();
        }
    }

    private bool Append(in PreparedBatch batch)
    {
        if (batch.EntryCount <= 0)
            return false;

        _document.ApplyPreparedBatch(batch);
        _metrics.RecordRendered(batch.EntryCount);
        return true;
    }

    /// <summary>
    /// Under load, trims in steps of a tenth of the document. Scintilla remembers the lines waiting for word wrap
    /// as one range, so a trim at the start plus an append at the end queues the whole document to be wrapped
    /// again, and doing that on every drain keeps the UI thread busy for as long as logs keep coming.
    /// The document returns to exactly <see cref="ScintillaOptions.MaxLines"/> as soon as the logs pause.
    /// </summary>
    private void TrimInSteps()
    {
        var overflow = GetOverflowLines();
        if (overflow <= 0)
            return;

        var step = Math.Max(LogConstants.MinTrimStepLines, _options.MaxLines / 10);
        Volatile.Write(ref _trimPending, overflow < step ? 1 : 0);
        if (overflow >= step)
            _document.TrimHeadLines(overflow);
    }

    private void TrimExactly()
    {
        if (_closed)
            return;

        // Waits like the drain does, and the pump asks again once it is idle.
        if (_control.IsParked)
        {
            Volatile.Write(ref _trimPending, 1);
            return;
        }

        var overflow = GetOverflowLines();
        if (overflow <= 0)
            return;

        _document.TrimHeadLines(overflow);
        _document.RefreshSearch();
    }

    /// <summary>Scintilla counts the empty line after the final line break, which holds no message.</summary>
    private int GetOverflowLines() => _document.GetLineCount() - 1 - _options.MaxLines;
}
