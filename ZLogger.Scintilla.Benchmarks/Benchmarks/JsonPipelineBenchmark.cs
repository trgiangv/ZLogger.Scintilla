using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Benchmarks.Benchmarking;
using ZLogger.Scintilla.Benchmarks.Scenarios;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Benchmarks.Benchmarks;

/// <summary>
/// Measures the overhead of the JSON pipeline (LookupEngine decomposition + pretty-print)
/// by comparing ingest + drain with EnablePrettyJson on vs off.
/// Uses structured messages with complex objects that trigger JSON decomposition.
/// </summary>
[Config(typeof(InProcessBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class JsonPipelineBenchmark
{
    private const int MessageCount = 5_000;

    private ScintillaLogViewer _viewerJsonOff = null!;
    private ScintillaLogViewer _viewerJsonOn = null!;

    private IReadOnlyList<byte[]> _plainUtf8 = null!;
    private IReadOnlyList<byte[]> _structuredUtf8 = null!;

    [GlobalSetup]
    public void Setup()
    {
        var plainMessages = ScenarioDataFactory.BuildMessages(
            MessageCount, sizeBytes: 128, TokenDensity.None, structuredPayload: false);
        _plainUtf8 = ScenarioDataFactory.BuildUtf8Messages(plainMessages);

        var structuredMessages = ScenarioDataFactory.BuildMessages(
            MessageCount, sizeBytes: 256, TokenDensity.Medium, structuredPayload: true);
        _structuredUtf8 = ScenarioDataFactory.BuildUtf8Messages(structuredMessages);

        _viewerJsonOff = BenchmarkHarness.CreateScintillaViewer(
            maxLines: 50_000, maxHistory: 0, disableHistory: true,
            maxBatchSize: 4096, flushIntervalMs: 1);
        BenchmarkHarness.PrepareOffscreenControl(_viewerJsonOff.ScintillaControl);
        _viewerJsonOff.Start();

        _viewerJsonOn = BenchmarkHarness.CreateScintillaViewer(
            maxLines: 50_000, maxHistory: 0, disableHistory: true,
            maxBatchSize: 4096, flushIntervalMs: 1);
        BenchmarkHarness.PrepareOffscreenControl(_viewerJsonOn.ScintillaControl);
        _viewerJsonOn.SetPrettyJson(true);
        _viewerJsonOn.Start();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _viewerJsonOff.Clear();
        _viewerJsonOn.Clear();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _viewerJsonOff.Stop();
        _viewerJsonOff.Dispose();
        _viewerJsonOn.Stop();
        _viewerJsonOn.Dispose();
    }

    // ── Enqueue: plain text ─────────────────────────────────────────────

    [BenchmarkCategory("Plain-Enqueue"), Benchmark(Baseline = true, Description = "Plain text, JSON off")]
    public long PlainText_JsonOff_Enqueue()
    {
        BenchmarkHarness.IngestToScintilla(_viewerJsonOff, _plainUtf8);
        return _viewerJsonOff.PendingEntryCount;
    }

    [BenchmarkCategory("Plain-Enqueue"), Benchmark(Description = "Plain text, JSON on")]
    public long PlainText_JsonOn_Enqueue()
    {
        BenchmarkHarness.IngestToScintilla(_viewerJsonOn, _plainUtf8);
        return _viewerJsonOn.PendingEntryCount;
    }

    // ── Enqueue: structured ─────────────────────────────────────────────

    [BenchmarkCategory("Structured-Enqueue"), Benchmark(Baseline = true, Description = "Structured, JSON off")]
    public long Structured_JsonOff_Enqueue()
    {
        BenchmarkHarness.IngestToScintilla(_viewerJsonOff, _structuredUtf8);
        return _viewerJsonOff.PendingEntryCount;
    }

    [BenchmarkCategory("Structured-Enqueue"), Benchmark(Description = "Structured, JSON on")]
    public long Structured_JsonOn_Enqueue()
    {
        BenchmarkHarness.IngestToScintilla(_viewerJsonOn, _structuredUtf8);
        return _viewerJsonOn.PendingEntryCount;
    }

    // ── Full pipeline: structured ───────────────────────────────────────

    [BenchmarkCategory("Structured-Full"), Benchmark(Baseline = true, Description = "Structured full, JSON off")]
    public long Structured_JsonOff_Full()
    {
        BenchmarkHarness.IngestToScintilla(_viewerJsonOff, _structuredUtf8);
        BenchmarkHarness.WaitForScintillaDrain(_viewerJsonOff, MessageCount);
        return _viewerJsonOff.RenderedMessages;
    }

    [BenchmarkCategory("Structured-Full"), Benchmark(Description = "Structured full, JSON on")]
    public long Structured_JsonOn_Full()
    {
        BenchmarkHarness.IngestToScintilla(_viewerJsonOn, _structuredUtf8);
        BenchmarkHarness.WaitForScintillaDrain(_viewerJsonOn, MessageCount);
        return _viewerJsonOn.RenderedMessages;
    }
}
