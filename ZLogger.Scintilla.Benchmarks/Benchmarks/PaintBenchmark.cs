using BenchmarkDotNet.Attributes;
using ZLogger.Scintilla.Benchmarks.Benchmarking;
using ZLogger.Scintilla.Benchmarks.Scenarios;
using Serilog.Sinks.RichTextBoxForms;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Benchmarks.Benchmarks;

/// <summary>
/// Isolates pure pixel-draw cost (WM_PRINTCLIENT) on a pre-filled 5k-line colorized document.
/// No ingest during measurement — only paint overhead.
/// </summary>
[Config(typeof(InProcessBenchmarkConfig))]
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class PaintBenchmark
{
    private const int DocumentSize = 5_000;

    private RealisticMixedData _data = null!;

    private ScintillaLogViewer _scintillaViewer = null!;
    private BenchmarkHarness.OffscreenPainter _scintillaPainter = null!;

    private RichTextBox _serilogProdRtb = null!;
    private RichTextBoxSink _serilogProdSink = null!;
    private BenchmarkHarness.OffscreenPainter _serilogProdPainter = null!;

    private RichTextBox _serilogUncapRtb = null!;
    private RichTextBoxSink _serilogUncapSink = null!;
    private BenchmarkHarness.OffscreenPainter _serilogUncapPainter = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = ScenarioDataFactory.BuildRealisticMixed(DocumentSize, seed: 55555);

        _scintillaViewer = BenchmarkHarness.CreateScintillaViewer(
            maxLines: 300_000, maxHistory: 300_000, disableHistory: false);
        BenchmarkHarness.PrepareOffscreenControl(_scintillaViewer.ScintillaControl);
        _scintillaViewer.Start();
        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        BenchmarkHarness.WaitForScintillaDrain(_scintillaViewer, DocumentSize, timeoutMs: 5000);
        _scintillaPainter = new BenchmarkHarness.OffscreenPainter(
            _scintillaViewer.ScintillaControl.ClientSize.Width,
            _scintillaViewer.ScintillaControl.ClientSize.Height);

        (_serilogProdRtb, var prodLogger, _serilogProdSink) = BenchmarkHarness.CreateSerilogProductionSink();
        BenchmarkHarness.EmitToSerilog(prodLogger, _data.Messages, _data.Levels);
        BenchmarkHarness.WaitForRichTextStable(_serilogProdRtb);
        _serilogProdPainter = new BenchmarkHarness.OffscreenPainter(
            _serilogProdRtb.ClientSize.Width, _serilogProdRtb.ClientSize.Height);

        (_serilogUncapRtb, var uncapLogger, _serilogUncapSink) = BenchmarkHarness.CreateSerilogUncappedSink(50_000);
        BenchmarkHarness.EmitToSerilog(uncapLogger, _data.Messages, _data.Levels);
        BenchmarkHarness.WaitForRichTextStable(_serilogUncapRtb);
        _serilogUncapPainter = new BenchmarkHarness.OffscreenPainter(
            _serilogUncapRtb.ClientSize.Width, _serilogUncapRtb.ClientSize.Height);
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

    // ── Production ───────────────────────────────────────────────────────

    [BenchmarkCategory("Production"), Benchmark(Baseline = true, Description = "RTB paint prod")]
    public int RichTextPaintProduction() => _serilogProdPainter.PaintAndHash(_serilogProdRtb);

    [BenchmarkCategory("Production"), Benchmark(Description = "Scintilla paint prod")]
    public int ScintillaPaintProduction() => _scintillaPainter.PaintAndHash(_scintillaViewer.ScintillaControl);

    // ── Uncapped ─────────────────────────────────────────────────────────

    [BenchmarkCategory("Uncapped"), Benchmark(Baseline = true, Description = "RTB paint uncap")]
    public int RichTextPaintUncapped() => _serilogUncapPainter.PaintAndHash(_serilogUncapRtb);

    [BenchmarkCategory("Uncapped"), Benchmark(Description = "Scintilla paint uncap")]
    public int ScintillaPaintUncapped() => _scintillaPainter.PaintAndHash(_scintillaViewer.ScintillaControl);
}
