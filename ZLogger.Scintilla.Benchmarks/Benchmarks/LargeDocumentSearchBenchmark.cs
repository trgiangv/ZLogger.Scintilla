using System.Text;
using BenchmarkDotNet.Attributes;
using ZLogger.Scintilla.Benchmarks.Benchmarking;
using ZLogger.Scintilla.Benchmarks.Scenarios;
using Serilog.Sinks.RichTextBoxForms;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Benchmarks.Benchmarks;

/// <summary>
/// Measures search performance on a pre-filled 50k-line document.
/// Tests: HighlightSearch + FindNext on a common token, and FindNext on a rare/absent token.
/// </summary>
[Config(typeof(InProcessBenchmarkConfig))]
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class LargeDocumentSearchBenchmark
{
    private const int DocumentSize = 50_000;
    private const string CommonToken = "api-gateway";
    private const string RareToken = "TOKEN_NOT_FOUND_987654321";

    private RealisticMixedData _data = null!;
    private string _richTextSnapshot = string.Empty;

    private ScintillaLogViewer _scintillaViewer = null!;
    private BenchmarkHarness.OffscreenPainter _scintillaPainter = null!;

    private RichTextBox _serilogUncapRtb = null!;
    private Serilog.ILogger _serilogUncapLogger = null!;
    private RichTextBoxSink _serilogUncapSink = null!;
    private BenchmarkHarness.OffscreenPainter _serilogUncapPainter = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = ScenarioDataFactory.BuildRealisticMixed(DocumentSize, seed: 33333);

        _scintillaViewer = BenchmarkHarness.CreateScintillaViewer(
            maxLines: 300_000, maxHistory: 300_000, disableHistory: false);
        BenchmarkHarness.PrepareOffscreenControl(_scintillaViewer.ScintillaControl);
        _scintillaViewer.Start();
        _scintillaPainter = new BenchmarkHarness.OffscreenPainter(
            _scintillaViewer.ScintillaControl.ClientSize.Width,
            _scintillaViewer.ScintillaControl.ClientSize.Height);

        BenchmarkHarness.IngestToScintilla(_scintillaViewer, _data.Utf8Messages, _data.Levels);
        BenchmarkHarness.WaitForScintillaDrain(_scintillaViewer, DocumentSize, timeoutMs: 10_000);

        (_serilogUncapRtb, _serilogUncapLogger, _serilogUncapSink) = BenchmarkHarness.CreateSerilogUncappedSink(300_000);
        _serilogUncapPainter = new BenchmarkHarness.OffscreenPainter(
            _serilogUncapRtb.ClientSize.Width, _serilogUncapRtb.ClientSize.Height);

        var sb = new StringBuilder(DocumentSize * 128);
        for (var i = 0; i < _data.Messages.Count; i++)
            sb.AppendLine(_data.Messages[i]);
        _richTextSnapshot = sb.ToString();
        _serilogUncapRtb.Text = _richTextSnapshot;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _scintillaViewer.Stop();
        _scintillaPainter.Dispose();
        _serilogUncapPainter.Dispose();
        _scintillaViewer.Dispose();
        _serilogUncapSink.Dispose();
        _serilogUncapRtb.Dispose();
    }

    // ── Highlight common token ───────────────────────────────────────────

    [BenchmarkCategory("HighlightCommon"), Benchmark(Baseline = true, Description = "RTB Find common token")]
    public int RichTextFindCommon()
    {
        _serilogUncapRtb.Select(0, 0);
        var pos = _serilogUncapRtb.Find(CommonToken, RichTextBoxFinds.None);
        return pos ^ _serilogUncapPainter.PaintAndHash(_serilogUncapRtb);
    }

    [BenchmarkCategory("HighlightCommon"), Benchmark(Description = "Scintilla Highlight+FindNext common")]
    public int ScintillaFindCommon()
    {
        _scintillaViewer.HighlightSearch(CommonToken);
        _scintillaViewer.ScintillaControl.CurrentPosition = 0;
        var pos = _scintillaViewer.FindNext();
        return (pos.Found ? pos.Current : -1) ^ _scintillaPainter.PaintAndHash(_scintillaViewer.ScintillaControl);
    }

    // ── Search rare/absent token ─────────────────────────────────────────

    [BenchmarkCategory("SearchRare"), Benchmark(Baseline = true, Description = "RTB Find rare token")]
    public int RichTextFindRare()
    {
        _serilogUncapRtb.Select(0, 0);
        return _serilogUncapRtb.Find(RareToken, RichTextBoxFinds.MatchCase);
    }

    [BenchmarkCategory("SearchRare"), Benchmark(Description = "Scintilla FindNext rare token")]
    public int ScintillaFindRare()
    {
        _scintillaViewer.HighlightSearch(RareToken);
        _scintillaViewer.ScintillaControl.CurrentPosition = 0;
        var pos = _scintillaViewer.FindNext();
        return pos.Found ? pos.Current : -1;
    }
}
