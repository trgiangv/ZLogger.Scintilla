using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Demo;

internal sealed class MainForm : Form
{
    private readonly ScintillaLogViewer _viewer;
    private readonly ILogger<MainForm> _logger;
    private readonly System.Windows.Forms.Timer _metricsTimer;

    private readonly Button _startFloodButton = new() { Text = "Start Flood", Width = 110 };
    private readonly Button _stopFloodButton = new() { Text = "Stop Flood", Width = 110, Enabled = false };
    private readonly Button _clearButton = new() { Text = "Clear", Width = 80 };
    private readonly Button _emitSamplesButton = new() { Text = "Emit Samples", Width = 110 };

    private readonly TextBox _searchTextBox = new() { Width = 240 };
    private readonly CheckBox _regexCheckBox = new() { AutoSize = true, Text = "Regex" };
    private readonly Button _findNextButton = new() { Text = "Find Next", Width = 100 };
    private readonly Button _findPreviousButton = new() { Text = "Find Previous", Width = 110 };
    private readonly ComboBox _levelFilterCombo = new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _prettyJsonCheckBox = new() { AutoSize = true, Text = "PrettyJson", Checked = true };
    private readonly CheckBox _lineNumbersCheckBox = new() { AutoSize = true, Text = "Line Numbers" };
    private readonly ComboBox _themeCombo = new() { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _searchStatusLabel = new() { AutoSize = true, Text = "matches: 0" };
    private readonly Label _metricsLabel = new() { AutoSize = true };
    private readonly System.Windows.Forms.Timer _searchDebounceTimer = new() { Interval = 180 };

    private CancellationTokenSource? _floodCts;
    private CancellationTokenSource? _searchCountCts;
    private Task? _floodTask;
    private long _incomingCount;
    private long _lastIncomingCount;
    private long _lastRenderedCount;
    private bool _smokeMode;

    public MainForm(
        ScintillaLogViewer viewer,
        ILogger<MainForm> logger)
    {
        Text = "ZLogger.Scintilla Demo";
        Width = 1450;
        Height = 900;

        _viewer = viewer;
        _logger = logger;

        BuildLayout();
        HookEvents();
        SeedInitialLogs();

        _metricsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _metricsTimer.Tick += (_, _) => RefreshMetrics();
        _metricsTimer.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _metricsTimer.Stop();
        _searchDebounceTimer.Stop();
        _searchCountCts?.Cancel();
        _searchCountCts?.Dispose();
        _searchCountCts = null;
        StopFloodAsync().GetAwaiter().GetResult();
        _viewer.Stop();
        _viewer.Dispose();
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _viewer.Start();
        if (_smokeMode)
            _ = RunSmokeScenarioAsync();
    }

    internal void EnableSmokeMode() => _smokeMode = true;

    private void BuildLayout()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight
        };

        _levelFilterCombo.Items.AddRange(new object[]
        {
            "All",
            nameof(LogLevel.Trace),
            nameof(LogLevel.Debug),
            nameof(LogLevel.Information),
            nameof(LogLevel.Warning),
            nameof(LogLevel.Error),
            nameof(LogLevel.Critical)
        });
        _levelFilterCombo.SelectedIndex = 0;
        _themeCombo.Items.AddRange(new object[] { "Dark", "Light" });
        _themeCombo.SelectedItem = "Dark";
        toolbar.Controls.Add(_startFloodButton);
        toolbar.Controls.Add(_stopFloodButton);
        toolbar.Controls.Add(_clearButton);
        toolbar.Controls.Add(_emitSamplesButton);

        toolbar.Controls.Add(new Label { Text = "Level:", AutoSize = true, Margin = new Padding(12, 8, 0, 0) });
        toolbar.Controls.Add(_levelFilterCombo);
        toolbar.Controls.Add(_prettyJsonCheckBox);
        toolbar.Controls.Add(_lineNumbersCheckBox);
        toolbar.Controls.Add(new Label { Text = "Theme:", AutoSize = true, Margin = new Padding(12, 8, 0, 0) });
        toolbar.Controls.Add(_themeCombo);
        toolbar.Controls.Add(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(12, 8, 0, 0) });
        toolbar.Controls.Add(_searchTextBox);
        toolbar.Controls.Add(_regexCheckBox);
        toolbar.Controls.Add(_findNextButton);
        toolbar.Controls.Add(_findPreviousButton);
        toolbar.Controls.Add(_searchStatusLabel);
        toolbar.Controls.Add(new Label { Text = "  ", AutoSize = true });
        toolbar.Controls.Add(_metricsLabel);

        Controls.Add(_viewer.ScintillaControl);
        Controls.Add(toolbar);
    }

    private void HookEvents()
    {
        _startFloodButton.Click += (_, _) => StartFlood();
        _stopFloodButton.Click += async (_, _) => await StopFloodAsync();
        _clearButton.Click += (_, _) =>
        {
            _viewer.Clear();
            Interlocked.Exchange(ref _incomingCount, 0);
        };
        _emitSamplesButton.Click += (_, _) => EmitShowcaseLogs();
        _findNextButton.Click += (_, _) => FindNext();
        _findPreviousButton.Click += (_, _) => FindPrevious();
        _searchTextBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                _searchDebounceTimer.Stop();
                FindNext();
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            HighlightSearchVisible();
        };
        _searchTextBox.TextChanged += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        };
        _levelFilterCombo.SelectedIndexChanged += (_, _) =>
            _viewer.SetFilter(ParseFilterLevel(_levelFilterCombo.SelectedItem?.ToString()));
        _prettyJsonCheckBox.CheckedChanged += (_, _) =>
            _viewer.SetPrettyJson(_prettyJsonCheckBox.Checked);
        _lineNumbersCheckBox.CheckedChanged += (_, _) =>
            _viewer.SetLineNumbers(_lineNumbersCheckBox.Checked);
        _themeCombo.SelectedIndexChanged += (_, _) =>
        {
            var isLight = string.Equals(_themeCombo.SelectedItem?.ToString(), "Light", StringComparison.OrdinalIgnoreCase);
            _viewer.SetTheme(isLight ? ScintillaThemes.Light : ScintillaThemes.Dark);
        };
    }

    private void SeedInitialLogs()
    {
        for (var i = 0; i < 100; i++)
        {
            var elementId = new ElementId(i + 1000);
            using var _ = _logger.BeginScope(new Dictionary<string, object>
            {
                ["Session"] = "Warmup",
                ["WarmupIndex"] = i,
                ["ElementId"] = elementId
            });
            _logger.ZLogInformation(
                $"Aurecon warmup log line index={i}, elementId={elementId}, uniqueId={"8d2f3f31-2b9f-4f6e-a18d-3e7a8f3b1c2d-0001a2b3"}, ifcGuid={"2Zw$n7f8AJvQk4r7sP9mA$"}, url={"https://example.com/revit"}");
        }
        _logger.ZLogInformation($"{new
        {
            eventName = "warmup",
            state = "ok",
            count = 100,
            nested = new { pretty = true }
        }}");
        EmitShowcaseLogs();
    }

    private void EmitShowcaseLogs()
    {
        _logger.ZLogInformation($"=== LOG LEVEL DETECTION TESTS ===");
        _logger.ZLogInformation($"[INFO] This should be Information level");
        _logger.ZLogWarning($"[WARN] This should be Warning level");
        _logger.ZLogError($"[ERROR] This should be Error level");
        _logger.ZLogCritical($"[FATAL] This should be Critical level");
        _logger.ZLogDebug($"[DEBUG] This should be Debug level");
        _logger.ZLogInformation($"Operation completed successfully");
        _logger.ZLogWarning($"Warning: Memory usage is high");
        _logger.ZLogError($"Error occurred during processing");
        _logger.ZLogCritical($"Fatal crash detected in system");
        _logger.ZLogDebug($"Just a regular debug message");
        _logger.ZLogInformation($"Cache hit ratio: {0.856:P2}");
        _logger.ZLogInformation($"Response time: {DateTime.Now}");
        _logger.ZLogInformation($"Is valid: {true}");
        _logger.ZLogInformation($"API version: {"2.1.0"}");
        _logger.ZLogWarning($"Memory usage: {1024} MB (threshold: {2048} MB)");
        _logger.ZLogError($"Failed order {"ORD-12345"} with code {"E500"}");

        var simpleMetrics = new { CPU = 85.5, Memory = 1024, Connections = 42 };
        _logger.ZLogInformation($"{simpleMetrics}");

        var apiRequest = new
        {
            Method = "POST",
            Endpoint = "/api/users",
            RequestId = Guid.NewGuid(),
            Headers = new { ContentType = "application/json", Authorization = "Bearer ***" }
        };
        _logger.ZLogInformation($"{apiRequest}");

        var classPayload = new DemoStructuredPayload
        {
            UserId = "class-user-01",
            Action = "SyncModel",
            DocsUrl = "https://learn.microsoft.com/dotnet/",
            SupportMail = "mailto:class-support@example.com",
            RetryCount = 3,
            TimestampUtc = DateTime.UtcNow,
            Details = new DemoStructuredDetails
            {
                HostName = "rvt-host-01",
                Environment = "Production",
                Thresholds = new[] { 512, 1024, 2048 }
            }
        };
        _logger.ZLogInformation($"{classPayload}");

        var apiConfig = new
        {
            BaseUrl = "https://api.example.com/v2",
            DocsUrl = "https://learn.microsoft.com/dotnet/api",
            HealthCheck = "https://status.example.com/health",
            Endpoints = new[]
            {
                "https://api.example.com/v2/users",
                "https://api.example.com/v2/orders"
            }
        };
        _logger.ZLogInformation($"API config loaded: {apiConfig}");

        _logger.ZLogInformation($"=== IEnumerable PARAMETER TESTS ===");
        
        
        var ids = new List<ElementId> { new ElementId(1001), new ElementId(1002), new ElementId(1003) };
        _logger.ZLogInformation($"Selected element IDs: {ids}");

        var names = new[] { "Wall", "Floor", "Roof", "Column", "Beam" };
        _logger.ZLogInformation($"Category names: {names}");

        var mixed = new List<string> { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta" };
        _logger.ZLogWarning($"Large collection (truncated by pretty-json): {mixed}");

        var points = new List<object> { new { X = 1.5, Y = 2.3 }, new { X = 4.0, Y = 5.1 } };
        _logger.ZLogDebug($"Geometry points: {points}");

        var empty = new List<string>();
        _logger.ZLogInformation($"Empty collection: {empty}");

        _logger.ZLogDebug($"=== EXCEPTION TEST ===");
        try
        {
            throw new InvalidOperationException("Test exception", new Exception("Inner exception"));
        }
        catch (Exception ex)
        {
            _logger.ZLogError($"Exception caught: {ex.Message}");
            _logger.ZLogDebug($"{ex}");
        }
    }

    private void StartFlood()
    {
        if (_floodCts != null)
            return;

        _floodCts = new CancellationTokenSource();
        var token = _floodCts.Token;
        _startFloodButton.Enabled = false;
        _stopFloodButton.Enabled = true;

        _floodTask = Task.Run(async () =>
        {
            var i = 0;
            while (!token.IsCancellationRequested)
            {
                var id = i++;
                Interlocked.Increment(ref _incomingCount);
                using var _ = _logger.BeginScope(new Dictionary<string, object?>
                {
                    ["Flow"] = "Flood",
                    ["Batch"] = id / 1000,
                    ["ElementId"] = id % 200_000
                });
                _logger.ZLogInformation(
                    $"flood index={id}, traceId={Guid.NewGuid()}, uniqueId={"8d2f3f31-2b9f-4f6e-a18d-3e7a8f3b1c2d-0001a2b3"}, ifcGuid={"2Zw$n7f8AJvQk4r7sP9mA$"}");
                if (id % 10 == 0)
                    _logger.ZLogWarning($"Warning sample at index={id}");
                if (id % 41 == 0)
                    _logger.ZLogError($"Failure at index={id}");
                if (id % 1000 == 0)
                    await Task.Delay(5, token).ConfigureAwait(false);
            }
        }, token);
    }

    private async Task StopFloodAsync()
    {
        if (_floodCts == null)
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
        _startFloodButton.Enabled = true;
        _stopFloodButton.Enabled = false;
    }

    private void FindNext()
    {
        var pattern = _searchTextBox.Text;
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        var pos = _viewer.FindNext();
        UpdateSearchStatus(pos);
    }

    private void FindPrevious()
    {
        var pattern = _searchTextBox.Text;
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        var pos = _viewer.FindPrevious();
        UpdateSearchStatus(pos);
    }

    private void HighlightSearchVisible()
    {
        var pattern = _searchTextBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(pattern))
        {
            _viewer.ClearSearch();
            _searchStatusLabel.Text = "matches: 0";
            return;
        }

        _viewer.HighlightSearch(pattern, _regexCheckBox.Checked);
        ScheduleTotalMatchCount(pattern);
    }

    private void UpdateSearchStatus(SearchPosition pos)
    {
        _searchStatusLabel.Text = pos.Found ? pos.ToString() : "not found";
    }

    private void ScheduleTotalMatchCount(string pattern)
    {
        _searchCountCts?.Cancel();
        _searchCountCts?.Dispose();
        _searchCountCts = null;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            _searchStatusLabel.Text = "matches: 0";
            return;
        }

        _searchStatusLabel.Text = "matches: ...";
        var cts = new CancellationTokenSource();
        _searchCountCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, cts.Token).ConfigureAwait(false);
                var total = await _viewer.CountMatchesAsync(cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested || IsDisposed)
                    return;

                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed && _searchCountCts == cts)
                        _searchStatusLabel.Text = $"matches: {total:n0}";
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

        var incomingRate = incoming - Interlocked.Exchange(ref _lastIncomingCount, incoming);
        var renderedRate = rendered - Interlocked.Exchange(ref _lastRenderedCount, rendered);
        var process = Process.GetCurrentProcess();
        var workingSetMb = process.WorkingSet64 / 1024d / 1024d;
        var managedMb = GC.GetTotalMemory(false) / 1024d / 1024d;

        _metricsLabel.Text =
            $"incoming/s: {incomingRate:n0} | rendered/s: {renderedRate:n0} | dropped: {dropped:n0} | " +
            $"backlog: {backlog:n0} | history: {_viewer.HistoryEntries:n0} | " +
            $"ws: {workingSetMb:n1} MB | managed: {managedMb:n1} MB";
    }

    private async Task RunSmokeScenarioAsync()
    {
        try
        {
            StartFlood();
            await Task.Delay(1800).ConfigureAwait(true);

            _levelFilterCombo.SelectedItem = nameof(LogLevel.Error);
            _viewer.SetFilter(LogLevel.Error);
            await Task.Delay(600).ConfigureAwait(true);

            await StopFloodAsync().ConfigureAwait(true);
            var linesAfterFlood = GetCurrentLineCount();

            _viewer.Clear();
            await WaitForBacklogDrainedAsync(1200).ConfigureAwait(true);
            var linesAfterClear = GetCurrentLineCount();

            using var _ = _logger.BeginScope(new Dictionary<string, object?> { ["SmokeScope"] = "scope-filter" });
            _logger.ZLogError($"SMOKE scope-filter validation id={Guid.NewGuid()}");
            await Task.Delay(300).ConfigureAwait(true);

            _viewer.SetFilter(LogLevel.Error);
            _searchTextBox.Text = "scope-filter";
            _viewer.HighlightSearch("scope-filter");
            await Task.Delay(400).ConfigureAwait(true);
            var linesAfterScopeFilter = GetCurrentLineCount();

            var ok = linesAfterFlood > 0 && linesAfterClear <= 1 && linesAfterScopeFilter > 0;
            Console.WriteLine(
                $"SMOKE RESULT: {(ok ? "PASS" : "FAIL")} | floodLines={linesAfterFlood} | clearLines={linesAfterClear} | scopeFilterLines={linesAfterScopeFilter}");
            Environment.ExitCode = ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SMOKE RESULT: FAIL | exception={ex}");
            Environment.ExitCode = 1;
        }
        finally
        {
            Close();
        }
    }

    private int GetCurrentLineCount()
        => _viewer.ScintillaControl.Lines.Count;

    private async Task WaitForBacklogDrainedAsync(int timeoutMs)
    {
        var started = Environment.TickCount;
        while (unchecked(Environment.TickCount - started) < timeoutMs)
        {
            if (_viewer.PendingEntryCount <= 0)
            {
                await Task.Delay(120).ConfigureAwait(true);
                return;
            }
            await Task.Delay(80).ConfigureAwait(true);
        }
    }

    private static LogLevel ParseFilterLevel(string? levelText)
    {
        if (Enum.TryParse<LogLevel>(levelText, ignoreCase: true, out var level))
            return level;

        return LogLevel.Trace;
    }
}

