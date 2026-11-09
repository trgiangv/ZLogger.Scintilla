using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Controls;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;
using ZLogger.Scintilla.Rendering;
using ZLogger.Scintilla.Services;
namespace ZLogger.Scintilla.Public;

/// <summary>
/// Log viewer: producers post to a bounded channel, one pump prepares batches, the UI thread applies them.
/// <see cref="BatchPump"/> is the background half, <see cref="RenderQueue"/> the UI thread's half; this class
/// wires them to the control and holds the public API.
/// </summary>
public abstract class ScintillaLogViewerBase : IDisposable
{
    private readonly ScintillaDocument _document;
    private readonly LogRenderer _renderStrategy;
    private readonly ScintillaOptions _options;
    private readonly LogIngestMetrics _metrics = new();
    private ScintillaLogProcessor? _processor;
    private List<Action>? _disposeActions;
    private IScintillaTheme _currentTheme;
    private volatile bool _disposed;

    public ScintillaNET.Scintilla ScintillaControl { get; }

    internal bool IncludeScopes => _options.IncludeScopes;

    internal SearchCoordinator Search { get; }

    internal LogFindBar FindBar { get; }

    internal RenderQueue RenderQueue { get; }

    internal BatchPump Pump { get; }

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

        _document = new ScintillaDocument(control, resolved, _currentTheme);
        _renderStrategy = new LogRenderer(resolved.FontFamily, resolved.FontSize, resolved);
        RenderQueue = new RenderQueue(control, _document, resolved, _metrics, ReportError);
        Pump = new BatchPump(resolved, _metrics, RenderQueue, new BatchPreparer(_renderStrategy, ReportError), ReportError);
        Search = new SearchCoordinator(_document, Dispatch);
        FindBar = new LogFindBar(control, Search, _document, _currentTheme);

        ApplyInitialStylesDeferred();
    }

    public abstract object HostElement { get; }
    protected abstract void DisposeHostResources();

    public void Start()
    {
        ThrowIfDisposed();
        Pump.Start();
    }

    /// <summary>
    /// Stops the pump. Called off the UI thread, it returns once the pump has finished. On the UI thread it returns
    /// at once, because a colorizer or linkifier on the pump may be waiting for that thread; the pump then stops
    /// after its current step, and <see cref="Start"/> lets it finish before another one runs.
    /// </summary>
    public void Stop()
    {
        if (Pump.Stop(waitForPump: ScintillaControl.InvokeRequired))
            RenderQueue.OnPumpStopped();
    }

    public void Clear()
    {
        ThrowIfDisposed();
        Pump.Clear();
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
        Pump.RequestReplay();
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
        Pump.AttachProcessor(processor);
    }

    /// <summary>
    /// Shows entries at or above <paramref name="minimumLevel"/>. With history disabled, there is nothing to
    /// rebuild the document from, so the filter applies to new entries only and existing lines stay.
    /// </summary>
    public void SetFilter(LogLevel minimumLevel)
    {
        ThrowIfDisposed();
        Pump.SetFilter(ClampFilterLevel(minimumLevel));
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

        ScintillaControl.HandleCreated -= OnHandleCreated;
        RenderQueue.Close();
        Pump.Close(waitForPump: ScintillaControl.InvokeRequired);
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

    /// <summary>Styles and the native theme need the window, so they wait for it if it does not exist yet.</summary>
    private void ApplyInitialStylesDeferred()
    {
        if (ScintillaControl.IsHandleCreated)
        {
            _document.ApplyThemeStyles(_renderStrategy, _currentTheme);
            ApplyNativeTheme();
        }
        else
        {
            ScintillaControl.HandleCreated += OnHandleCreated;
        }
    }

    private void OnHandleCreated(object? sender, EventArgs e)
    {
        ScintillaControl.HandleCreated -= OnHandleCreated;
        _document.ApplyThemeStyles(_renderStrategy, _currentTheme);
        ApplyNativeTheme();
        RenderQueue.Schedule();
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
