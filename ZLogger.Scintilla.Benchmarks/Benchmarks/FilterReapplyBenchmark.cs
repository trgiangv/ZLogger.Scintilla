using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Benchmarks.Benchmarking;
using ZLogger.Scintilla.Benchmarks.Scenarios;
using Serilog.Sinks.RichTextBoxForms;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Benchmarks.Benchmarks;

/// <summary>
/// Measures filter reapply latency on a 50k-entry dataset.
///
/// Three benchmarks:
///
///   SerilogFilteredEmit  — Serilog's only option for "filter": iterate the in-memory
///                          snapshot, select entries >= Warning, re-emit each one into
///                          a fresh RichTextBoxSink, then wait for render.
///                          This is the real Serilog equivalent of filter reapply —
///                          there is no built-in history buffer, so the caller must
///                          hold the original data and re-emit the filtered subset.
///
///   ScintillaFilterReapply — SetFilter(Warning) on a viewer that already has 50k entries
///                          in its history buffer. The pipeline replays only the matching
///                          subset from history, clears the document, and re-renders.
///                          No re-emit required; the history buffer is the source of truth.
///
///   ManualScanOnly       — Pure CPU cost of scanning 50k entries for level >= Warning,
///                          with no rendering at all. Establishes the minimum possible
///                          baseline for any filter operation on this dataset.
/// </summary>
[Config(typeof(InProcessBenchmarkConfig))]
[MemoryDiagnoser]
public class FilterReapplyBenchmark
{
    private const int DocumentSize = 50_000;

    private RealisticMixedData _data = null!;

    // Scintilla viewer pre-filled with 50k history entries
    private ScintillaLogViewer _scintillaViewer = null!;

    // Serilog: fresh sink created each IterationSetup to receive the filtered re-emit
    private RichTextBox _serilogFilterRtb = null!;
    private Serilog.ILogger _serilogFilterLogger = null!;
    private RichTextBoxSink _serilogFilterSink = null!;

    // Pre-filtered subset for Serilog re-emit (indices of entries with Level >= Warning)
    private int[] _warningIndices = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = ScenarioDataFactory.BuildRealisticMixed(DocumentSize, seed: 44444);

        // Pre-compute warning-level indices once — avoids measuring index scan in Serilog benchmark
        var indices = new List<int>(DocumentSize / 4);
        for (var i = 0; i < _data.Levels.Count; i++)
        {
            if (_data.Levels[i] >= LogLevel.Warning)
                indices.Add(i);
        }
        _warningIndices = indices.ToArray();

        // Scintilla: pre-fill 50k entries into history
        _scintillaViewer = BenchmarkHarness.CreateScintillaViewer(
            maxLines: 300_000, maxHistory: 300_000, disableHistory: false,
            maxBatchSize: 4096, flushIntervalMs: 1);
        BenchmarkHarness.PrepareOffscreenControl(_scintillaViewer.ScintillaControl);
        _scintillaViewer.Start();

        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        BenchmarkHarness.WaitForScintillaDrain(_scintillaViewer, DocumentSize, timeoutMs: 10_000);

        // Serilog: create initial sink (will be recreated per iteration)
        (_serilogFilterRtb, _serilogFilterLogger, _serilogFilterSink) =
            BenchmarkHarness.CreateSerilogUncappedSink(DocumentSize);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Reset Scintilla filter to show all before each iteration, wait for full reapply
        _scintillaViewer.SetFilter(LogLevel.Trace);
        BenchmarkHarness.WaitForFilterReapply(_scintillaViewer, timeoutMs: 5000);

        // Recreate Serilog sink so each iteration starts from empty
        _serilogFilterSink.Dispose();
        _serilogFilterRtb.Dispose();
        (_serilogFilterRtb, _serilogFilterLogger, _serilogFilterSink) =
            BenchmarkHarness.CreateSerilogUncappedSink(DocumentSize);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scintillaViewer.Stop();
        _scintillaViewer.Dispose();
        _serilogFilterSink.Dispose();
        _serilogFilterRtb.Dispose();
    }

    /// <summary>
    /// Serilog equivalent of filter reapply: iterate pre-computed warning indices,
    /// re-emit each matching entry into a fresh sink, wait for render.
    /// This is the only way Serilog can "filter" — the caller owns the data.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Serilog re-emit filtered subset (Warning+)")]
    public int SerilogFilteredEmit()
    {
        for (var i = 0; i < _warningIndices.Length; i++)
        {
            var idx = _warningIndices[i];
            var serilogLevel = BenchmarkHarness.MapToSerilogLevel(_data.Levels[idx]);
            _serilogFilterLogger.Write(serilogLevel, "{Message}", _data.Messages[idx]);
        }
        BenchmarkHarness.WaitForRichTextStable(_serilogFilterRtb, timeoutMs: 10_000);
        return _serilogFilterRtb.TextLength;
    }

    /// <summary>
    /// Scintilla SetFilter(Warning): replays matching entries from the internal history
    /// buffer, clears the document, and re-renders the filtered subset.
    /// No re-emit required — the history buffer is the source of truth.
    /// </summary>
    [Benchmark(Description = "Scintilla SetFilter(Warning) reapply from history")]
    public int ScintillaFilterReapply()
    {
        _scintillaViewer.SetFilter(LogLevel.Warning);
        BenchmarkHarness.WaitForFilterReapply(_scintillaViewer, timeoutMs: 10_000);
        return _scintillaViewer.ScintillaControl.Lines.Count;
    }

    /// <summary>
    /// Pure CPU cost of scanning 50k entries for level >= Warning with no rendering.
    /// Establishes the minimum possible baseline for any filter operation on this dataset.
    /// </summary>
    [Benchmark(Description = "CPU scan only (no render, lower bound)")]
    public int ManualScanOnly()
    {
        var count = 0;
        for (var i = 0; i < _data.Levels.Count; i++)
        {
            if (_data.Levels[i] >= LogLevel.Warning)
                count++;
        }
        return count;
    }
}
