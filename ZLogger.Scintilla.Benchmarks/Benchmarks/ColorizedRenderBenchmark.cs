using BenchmarkDotNet.Attributes;
using ZLogger.Scintilla.Benchmarks.Benchmarking;
using ZLogger.Scintilla.Benchmarks.Scenarios;
using Serilog.Sinks.RichTextBoxForms;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Benchmarks.Benchmarks;

/// <summary>
/// Measures colorized render pipeline for 2k structured JSON messages.
///
/// Two benchmark pairs per group:
///
///   *_Enqueue  — time to push all messages into the pipeline only (no wait for render).
///                Serilog: Emit() → ConcurrentCircularBuffer.Add().
///                Scintilla: TryPost() → BoundedChannel.TryWrite().
///                Fair comparison of producer-side throughput + serialization cost.
///
///   *_FullPipeline — time for full cycle: enqueue + render + pixel paint.
///                Serilog: emit → WaitForRichTextStable → WM_PRINTCLIENT.
///                Scintilla: TryPost → WaitForScintillaDrain → WM_PRINTCLIENT.
///                Both wait for the UI to finish rendering before painting.
///                Measures total end-to-end latency including colorization cost.
///
/// Two groups: Production (Serilog 2048-line cap) and Uncapped (50k fair compare).
/// </summary>
[Config(typeof(InProcessBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class ColorizedRenderBenchmark
{
    private const int BatchSize = 2_000;

    private RealisticMixedData _data = null!;

    private ScintillaLogViewer _scintillaViewer = null!;
    private BenchmarkHarness.OffscreenPainter _scintillaPainter = null!;

    private RichTextBox _serilogProdRtb = null!;
    private Serilog.ILogger _serilogProdLogger = null!;
    private RichTextBoxSink _serilogProdSink = null!;
    private BenchmarkHarness.OffscreenPainter _serilogProdPainter = null!;

    private RichTextBox _serilogUncapRtb = null!;
    private Serilog.ILogger _serilogUncapLogger = null!;
    private RichTextBoxSink _serilogUncapSink = null!;
    private BenchmarkHarness.OffscreenPainter _serilogUncapPainter = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = ScenarioDataFactory.BuildRealisticMixed(BatchSize, seed: 22222);

        _scintillaViewer = BenchmarkHarness.CreateScintillaViewer(
            maxLines: 50_000, maxHistory: 0, disableHistory: true,
            maxBatchSize: 8192, flushIntervalMs: 1);
        BenchmarkHarness.PrepareOffscreenControl(_scintillaViewer.ScintillaControl);
        _scintillaViewer.Start();
        _scintillaPainter = new BenchmarkHarness.OffscreenPainter(
            _scintillaViewer.ScintillaControl.ClientSize.Width,
            _scintillaViewer.ScintillaControl.ClientSize.Height);

        (_serilogProdRtb, _serilogProdLogger, _serilogProdSink) = BenchmarkHarness.CreateSerilogProductionSink();
        _serilogProdPainter = new BenchmarkHarness.OffscreenPainter(
            _serilogProdRtb.ClientSize.Width, _serilogProdRtb.ClientSize.Height);

        (_serilogUncapRtb, _serilogUncapLogger, _serilogUncapSink) = BenchmarkHarness.CreateSerilogUncappedSink(50_000);
        _serilogUncapPainter = new BenchmarkHarness.OffscreenPainter(
            _serilogUncapRtb.ClientSize.Width, _serilogUncapRtb.ClientSize.Height);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _scintillaViewer.Clear();
        _serilogProdSink.Clear();
        _serilogUncapSink.Clear();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scintillaViewer.Stop();
        _scintillaPainter.Dispose();
        _serilogProdPainter.Dispose();
        _serilogUncapPainter.Dispose();
        _scintillaViewer.Dispose();
        _serilogProdSink.Dispose();
        _serilogProdRtb.Dispose();
        _serilogUncapSink.Dispose();
        _serilogUncapRtb.Dispose();
    }

    // ── Production: Enqueue only ─────────────────────────────────────────

    [BenchmarkCategory("Production-Enqueue"), Benchmark(Baseline = true, Description = "Serilog+RTB colorized enqueue")]
    public int SerilogProduction_Enqueue()
    {
        BenchmarkHarness.EmitToSerilog(_serilogProdLogger, _data.Messages, _data.Levels);
        return _serilogProdRtb.TextLength;
    }

    [BenchmarkCategory("Production-Enqueue"), Benchmark(Description = "Scintilla colorized enqueue")]
    public long ScintillaProduction_Enqueue()
    {
        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        return _scintillaViewer.PendingEntryCount;
    }

    // ── Production: Full pipeline (enqueue + render + paint) ─────────────

    [BenchmarkCategory("Production-FullPipeline"), Benchmark(Baseline = true, Description = "Serilog+RTB colorized full")]
    public int SerilogProduction_FullPipeline()
    {
        BenchmarkHarness.EmitToSerilog(_serilogProdLogger, _data.Messages, _data.Levels);
        BenchmarkHarness.WaitForRichTextStable(_serilogProdRtb);
        return _serilogProdRtb.TextLength ^ _serilogProdPainter.PaintAndHash(_serilogProdRtb);
    }

    [BenchmarkCategory("Production-FullPipeline"), Benchmark(Description = "Scintilla colorized full")]
    public int ScintillaProduction_FullPipeline()
    {
        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        BenchmarkHarness.WaitForScintillaDrain(_scintillaViewer, BatchSize);
        return _scintillaViewer.ScintillaControl.TextLength ^ _scintillaPainter.PaintAndHash(_scintillaViewer.ScintillaControl);
    }

    // ── Uncapped: Enqueue only ───────────────────────────────────────────

    [BenchmarkCategory("Uncapped-Enqueue"), Benchmark(Baseline = true, Description = "Serilog+RTB uncap colorized enqueue")]
    public int SerilogUncapped_Enqueue()
    {
        BenchmarkHarness.EmitToSerilog(_serilogUncapLogger, _data.Messages, _data.Levels);
        return _serilogUncapRtb.TextLength;
    }

    [BenchmarkCategory("Uncapped-Enqueue"), Benchmark(Description = "Scintilla uncap colorized enqueue")]
    public long ScintillaUncapped_Enqueue()
    {
        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        return _scintillaViewer.PendingEntryCount;
    }

    // ── Uncapped: Full pipeline ──────────────────────────────────────────

    [BenchmarkCategory("Uncapped-FullPipeline"), Benchmark(Baseline = true, Description = "Serilog+RTB uncap colorized full")]
    public int SerilogUncapped_FullPipeline()
    {
        BenchmarkHarness.EmitToSerilog(_serilogUncapLogger, _data.Messages, _data.Levels);
        BenchmarkHarness.WaitForRichTextStable(_serilogUncapRtb);
        return _serilogUncapRtb.TextLength ^ _serilogUncapPainter.PaintAndHash(_serilogUncapRtb);
    }

    [BenchmarkCategory("Uncapped-FullPipeline"), Benchmark(Description = "Scintilla uncap colorized full")]
    public int ScintillaUncapped_FullPipeline()
    {
        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        BenchmarkHarness.WaitForScintillaDrain(_scintillaViewer, BatchSize);
        return _scintillaViewer.ScintillaControl.TextLength ^ _scintillaPainter.PaintAndHash(_scintillaViewer.ScintillaControl);
    }
}
