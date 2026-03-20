using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Wpf.Demo;

public partial class MainWindow
{
    private readonly ScintillaLogViewerWpf _viewer;
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherTimer _metricsTimer;
    private readonly DispatcherTimer _searchDebounceTimer;

    private CancellationTokenSource? _floodCts;
    private CancellationTokenSource? _searchCountCts;
    private Task? _floodTask;
    private long _incomingCount;
    private long _lastIncomingCount;
    private long _lastRenderedCount;

    public MainWindow(
        ScintillaLogViewerWpf viewer,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();

        _viewer = viewer;
        _logger = logger;

        ViewerContainer.Content = _viewer.HostElement;
        InitializeControls();
        HookEvents();
        SeedInitialLogs();

        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            HighlightSearchVisible();
        };

        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _metricsTimer.Tick += (_, _) => RefreshMetrics();
        _metricsTimer.Start();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _viewer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _metricsTimer.Stop();
        _searchDebounceTimer.Stop();
        _searchCountCts?.Cancel();
        _searchCountCts?.Dispose();
        _searchCountCts = null;
        StopFloodAsync().GetAwaiter().GetResult();
        _viewer.Stop();
        _viewer.Dispose();
        base.OnClosed(e);
    }

    private void InitializeControls()
    {
        LevelFilterCombo.ItemsSource = new[]
        {
            "All",
            nameof(LogLevel.Trace),
            nameof(LogLevel.Debug),
            nameof(LogLevel.Information),
            nameof(LogLevel.Warning),
            nameof(LogLevel.Error),
            nameof(LogLevel.Critical)
        };
        LevelFilterCombo.SelectedIndex = 0;
        PrettyJsonCheckBox.IsChecked = true;
        ThemeCombo.ItemsSource = new[] { "Dark", "Light" };
        ThemeCombo.SelectedItem = "Dark";
    }

    private void HookEvents()
    {
        StartFloodButton.Click += (_, _) => StartFlood();
        StopFloodButton.Click += async (_, _) => await StopFloodAsync();
        ClearButton.Click += (_, _) =>
        {
            _viewer.Clear();
            Interlocked.Exchange(ref _incomingCount, 0);
        };
        FindNextButton.Click += (_, _) => FindNext();
        FindPreviousButton.Click += (_, _) => FindPrevious();
        SearchTextBox.TextChanged += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        };
        SearchTextBox.KeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Enter)
            {
                _searchDebounceTimer.Stop();
                FindNext();
                args.Handled = true;
            }
        };
        LevelFilterCombo.SelectionChanged += (_, _) =>
            _viewer.SetFilter(ParseFilterLevel(LevelFilterCombo.SelectedItem?.ToString()));
        PrettyJsonCheckBox.Checked += (_, _) => _viewer.SetPrettyJson(true);
        PrettyJsonCheckBox.Unchecked += (_, _) => _viewer.SetPrettyJson(false);
        LineNumbersCheckBox.Checked += (_, _) => _viewer.SetLineNumbers(true);
        LineNumbersCheckBox.Unchecked += (_, _) => _viewer.SetLineNumbers(false);
        ThemeCombo.SelectionChanged += (_, _) =>
        {
            var isLight = string.Equals(ThemeCombo.SelectedItem?.ToString(), "Light", StringComparison.OrdinalIgnoreCase);
            _viewer.SetTheme(isLight ? ScintillaThemes.Light : ScintillaThemes.Dark);
        };
    }

    private void SeedInitialLogs()
    {
        for (var i = 0; i < 100; i++)
        {
            _logger.ZLogInformation(
                $"WPF warmup index={i}, uniqueId={"8d2f3f31-2b9f-4f6e-a18d-3e7a8f3b1c2d-0001a2b3"}, ifcGuid={"2Zw$n7f8AJvQk4r7sP9mA$"}");
        }
        _logger.ZLogInformation($"WPF demo started at {DateTime.Now}");
    }

    private void StartFlood()
    {
        if (_floodCts is not null)
            return;

        _floodCts = new CancellationTokenSource();
        var token = _floodCts.Token;
        StartFloodButton.IsEnabled = false;
        StopFloodButton.IsEnabled = true;

        _floodTask = Task.Run(async () =>
        {
            var i = 0;
            while (!token.IsCancellationRequested)
            {
                var id = i++;
                Interlocked.Increment(ref _incomingCount);
                _logger.ZLogInformation(
                    $"wpf flood index={id}, traceId={Guid.NewGuid()}, uniqueId={"8d2f3f31-2b9f-4f6e-a18d-3e7a8f3b1c2d-0001a2b3"}");
                if (id % 10 == 0) _logger.ZLogWarning($"WPF warning at index={id}");
                if (id % 41 == 0) _logger.ZLogError($"WPF failure at index={id}");
                if (id % 1000 == 0)
                    await Task.Delay(5, token).ConfigureAwait(false);
            }
        }, token);
    }

    private async Task StopFloodAsync()
    {
        if (_floodCts is null)
            return;

        _floodCts.Cancel();
        if (_floodTask is not null)
        {
            try { await _floodTask.ConfigureAwait(true); }
            catch (OperationCanceledException) { }
        }

        _floodTask = null;
        _floodCts.Dispose();
        _floodCts = null;
        StartFloodButton.IsEnabled = true;
        StopFloodButton.IsEnabled = false;
    }

    private void FindNext()
    {
        var pattern = SearchTextBox.Text;
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        var pos = _viewer.FindNext();
        SearchStatusTextBlock.Text = pos.Found ? pos.ToString() : "not found";
    }

    private void FindPrevious()
    {
        var pattern = SearchTextBox.Text;
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        var pos = _viewer.FindPrevious();
        SearchStatusTextBlock.Text = pos.Found ? pos.ToString() : "not found";
    }

    private void HighlightSearchVisible()
    {
        var pattern = SearchTextBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(pattern))
        {
            _viewer.ClearSearch();
            SearchStatusTextBlock.Text = "matches: 0";
            return;
        }

        _viewer.HighlightSearch(pattern);
        ScheduleTotalMatchCount(pattern);
    }

    private void ScheduleTotalMatchCount(string pattern)
    {
        _searchCountCts?.Cancel();
        _searchCountCts?.Dispose();
        _searchCountCts = null;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            SearchStatusTextBlock.Text = "matches: 0";
            return;
        }

        SearchStatusTextBlock.Text = "matches: ...";
        var cts = new CancellationTokenSource();
        _searchCountCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, cts.Token).ConfigureAwait(false);
                var total = await _viewer.CountMatchesAsync(cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested)
                    return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_searchCountCts == cts)
                        SearchStatusTextBlock.Text = $"matches: {total:n0}";
                }));
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    private void RefreshMetrics()
    {
        var incoming = Interlocked.Read(ref _incomingCount);
        var rendered = _viewer.RenderedMessages;
        var dropped = _viewer.DroppedMessages;
        var backlog = _viewer.PendingEntryCount;
        var lineCount = _viewer.ScintillaControl.Lines.Count;

        var incomingRate = incoming - Interlocked.Exchange(ref _lastIncomingCount, incoming);
        var renderedRate = rendered - Interlocked.Exchange(ref _lastRenderedCount, rendered);

        var process = Process.GetCurrentProcess();
        var workingSetMb = process.WorkingSet64 / 1024d / 1024d;
        var managedMb = GC.GetTotalMemory(false) / 1024d / 1024d;

        MetricsTextBlock.Text =
            $"incoming/s: {incomingRate:n0} | rendered/s: {renderedRate:n0} | dropped: {dropped:n0} | " +
            $"backlog: {backlog:n0} | history: {_viewer.HistoryEntries:n0} | lines: {lineCount:n0} | " +
            $"ws: {workingSetMb:n1} MB | managed: {managedMb:n1} MB";
    }

    private static LogLevel ParseFilterLevel(string? levelText)
    {
        if (Enum.TryParse<LogLevel>(levelText, ignoreCase: true, out var level))
            return level;

        return LogLevel.Trace;
    }
}
