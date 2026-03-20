using BenchmarkDotNet.Attributes;
using ZLogger.Scintilla.Benchmarks.Benchmarking;
using ZLogger.Scintilla.Benchmarks.Scenarios;
using Serilog.Sinks.RichTextBoxForms;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Benchmarks.Benchmarks;

/// <summary>
/// Sustained load with 100k messages.
///
/// Two benchmark pairs per group:
///
///   *_Enqueue  — time to push all 100k messages into the pipeline only.
///                Serilog: Emit() → ConcurrentCircularBuffer.Add() (lock-based).
///                Scintilla: TryPost() → BoundedChannel.TryWrite() (lock-free).
///                No wait for rendering. Measures raw producer-side throughput
///                and GC pressure from message allocation.
///
///   *_FullPipeline — enqueue + wait for full render completion.
///                Serilog: emit → WaitForRichTextStable.
///                Scintilla: TryPost → WaitForScintillaDrain.
///                Measures total end-to-end latency at sustained volume.
///                Scintilla will show higher latency due to async UI dispatch,
///                but lower allocation due to pooled byte arrays.
///
/// Two groups: Production (Serilog 2048-line cap) and Uncapped (50k fair compare).
/// </summary>
[Config(typeof(InProcessBenchmarkConfig))]
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class SustainedLoadBenchmark
{
    private const int TotalMessages = 100_000;

    private RealisticMixedData _data = null!;

    private ScintillaLogViewer _scintillaViewer = null!;

    private RichTextBox _serilogProdRtb = null!;
    private Serilog.ILogger _serilogProdLogger = null!;
    private RichTextBoxSink _serilogProdSink = null!;

    private RichTextBox _serilogUncapRtb = null!;
    private Serilog.ILogger _serilogUncapLogger = null!;
    private RichTextBoxSink _serilogUncapSink = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = ScenarioDataFactory.BuildRealisticMixed(TotalMessages, seed: 66666);

        _scintillaViewer = BenchmarkHarness.CreateScintillaViewer(
            maxLines: 50_000, maxHistory: 0, disableHistory: true,
            maxBatchSize: 4096, flushIntervalMs: 1);
        BenchmarkHarness.PrepareOffscreenControl(_scintillaViewer.ScintillaControl);
        _scintillaViewer.Start();

        (_serilogProdRtb, _serilogProdLogger, _serilogProdSink) = BenchmarkHarness.CreateSerilogProductionSink();
        (_serilogUncapRtb, _serilogUncapLogger, _serilogUncapSink) = BenchmarkHarness.CreateSerilogUncappedSink(50_000);
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
        _scintillaViewer.Dispose();
        _serilogProdSink.Dispose();
        _serilogProdRtb.Dispose();
        _serilogUncapSink.Dispose();
        _serilogUncapRtb.Dispose();
    }

    // ── Production: Enqueue only ─────────────────────────────────────────

    [BenchmarkCategory("Production-Enqueue"), Benchmark(Baseline = true, Description = "Serilog sustained enqueue 100k")]
    public int SerilogProduction_Enqueue()
    {
        BenchmarkHarness.EmitToSerilog(_serilogProdLogger, _data.Messages, _data.Levels);
        return _serilogProdRtb.TextLength;
    }

    [BenchmarkCategory("Production-Enqueue"), Benchmark(Description = "Scintilla sustained enqueue 100k")]
    public long ScintillaProduction_Enqueue()
    {
        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        return _scintillaViewer.PendingEntryCount;
    }

    // ── Production: Full pipeline ────────────────────────────────────────

    [BenchmarkCategory("Production-FullPipeline"), Benchmark(Baseline = true, Description = "Serilog sustained full 100k")]
    public int SerilogProduction_FullPipeline()
    {
        BenchmarkHarness.EmitToSerilog(_serilogProdLogger, _data.Messages, _data.Levels);
        BenchmarkHarness.WaitForRichTextStable(_serilogProdRtb, timeoutMs: 10_000);
        return _serilogProdRtb.TextLength;
    }

    [BenchmarkCategory("Production-FullPipeline"), Benchmark(Description = "Scintilla sustained full 100k")]
    public long ScintillaProduction_FullPipeline()
    {
        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        BenchmarkHarness.WaitForScintillaDrain(_scintillaViewer, timeoutMs: 10_000);
        return _scintillaViewer.RenderedMessages;
    }

    // ── Uncapped: Enqueue only ───────────────────────────────────────────

    [BenchmarkCategory("Uncapped-Enqueue"), Benchmark(Baseline = true, Description = "Serilog uncap sustained enqueue 100k")]
    public int SerilogUncapped_Enqueue()
    {
        BenchmarkHarness.EmitToSerilog(_serilogUncapLogger, _data.Messages, _data.Levels);
        return _serilogUncapRtb.TextLength;
    }

    [BenchmarkCategory("Uncapped-Enqueue"), Benchmark(Description = "Scintilla uncap sustained enqueue 100k")]
    public long ScintillaUncapped_Enqueue()
    {
        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        return _scintillaViewer.PendingEntryCount;
    }

    // ── Uncapped: Full pipeline ──────────────────────────────────────────

    [BenchmarkCategory("Uncapped-FullPipeline"), Benchmark(Baseline = true, Description = "Serilog uncap sustained full 100k")]
    public int SerilogUncapped_FullPipeline()
    {
        BenchmarkHarness.EmitToSerilog(_serilogUncapLogger, _data.Messages, _data.Levels);
        BenchmarkHarness.WaitForRichTextStable(_serilogUncapRtb, timeoutMs: 10_000);
        return _serilogUncapRtb.TextLength;
    }

    [BenchmarkCategory("Uncapped-FullPipeline"), Benchmark(Description = "Scintilla uncap sustained full 100k")]
    public long ScintillaUncapped_FullPipeline()
    {
        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        BenchmarkHarness.WaitForScintillaDrain(_scintillaViewer, timeoutMs: 10_000);
        return _scintillaViewer.RenderedMessages;
    }
}
