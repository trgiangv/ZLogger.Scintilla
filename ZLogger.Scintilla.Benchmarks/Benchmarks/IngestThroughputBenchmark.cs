using BenchmarkDotNet.Attributes;
using ZLogger.Scintilla.Benchmarks.Benchmarking;
using ZLogger.Scintilla.Benchmarks.Scenarios;
using Serilog.Sinks.RichTextBoxForms;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;
namespace ZLogger.Scintilla.Benchmarks.Benchmarks;

/// <summary>
/// Measures ingest throughput from 4 concurrent producers with 50k mixed messages.
///
/// Two benchmark pairs per group — each pair measures the SAME phase:
///
///   *_Enqueue  — time to push all messages into the ingest pipeline only.
///                Serilog: Emit() → ConcurrentCircularBuffer.Add() (lock-based).
///                Scintilla: TryPost() → BoundedChannel.TryWrite() (lock-free).
///                Both return as soon as the last producer thread joins.
///                Neither waits for UI rendering. This is a fair apples-to-apples
///                comparison of producer-side throughput.
///
///   *_Drain    — time for the UI to finish rendering everything already in the pipeline.
///                Serilog: WaitForRichTextStable() polls TextLength stability.
///                Scintilla: WaitForScintillaDrain() polls RenderedMessages counter.
///                Both enqueue inside the measured method, then wait for render.
///                Measures total end-to-end latency (enqueue + render).
///
/// Two groups: Production (Serilog 2048-line cap) and Uncapped (50k fair compare).
/// </summary>
[Config(typeof(InProcessBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class IngestThroughputBenchmark
{
    private const int TotalMessages = 50_000;
    private const int ProducerCount = 4;
    private const int MessagesPerProducer = TotalMessages / ProducerCount;

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
        _data = ScenarioDataFactory.BuildRealisticMixed(TotalMessages);

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

    // ── Production: Enqueue phase ────────────────────────────────────────
    // Measures only the time to push all messages into the pipeline.

    [BenchmarkCategory("Production-Enqueue"), Benchmark(Baseline = true, Description = "Serilog+RTB enqueue 50k")]
    public int SerilogProduction_Enqueue()
    {
        ParallelEmitSerilog(_serilogProdLogger);
        return _serilogProdRtb.TextLength;
    }

    [BenchmarkCategory("Production-Enqueue"), Benchmark(Description = "Scintilla enqueue 50k")]
    public long ScintillaProduction_Enqueue()
    {
        ParallelIngestScintilla();
        return _scintillaViewer.PendingEntryCount;
    }

    // ── Production: Drain phase ──────────────────────────────────────────
    // Pre-fills in IterationSetup (via GlobalSetup data), then measures render time.
    // Note: IterationSetup clears both sinks; we re-enqueue then measure drain.

    [BenchmarkCategory("Production-Drain"), Benchmark(Baseline = true, Description = "Serilog+RTB drain 50k")]
    public int SerilogProduction_Drain()
    {
        ParallelEmitSerilog(_serilogProdLogger);
        BenchmarkHarness.WaitForRichTextStable(_serilogProdRtb);
        return _serilogProdRtb.TextLength;
    }

    [BenchmarkCategory("Production-Drain"), Benchmark(Description = "Scintilla drain 50k")]
    public long ScintillaProduction_Drain()
    {
        ParallelIngestScintilla();
        BenchmarkHarness.WaitForScintillaDrain(_scintillaViewer, TotalMessages);
        return _scintillaViewer.RenderedMessages;
    }

    // ── Uncapped: Enqueue phase ──────────────────────────────────────────

    [BenchmarkCategory("Uncapped-Enqueue"), Benchmark(Baseline = true, Description = "Serilog+RTB uncap enqueue 50k")]
    public int SerilogUncapped_Enqueue()
    {
        ParallelEmitSerilog(_serilogUncapLogger);
        return _serilogUncapRtb.TextLength;
    }

    [BenchmarkCategory("Uncapped-Enqueue"), Benchmark(Description = "Scintilla uncap enqueue 50k")]
    public long ScintillaUncapped_Enqueue()
    {
        ParallelIngestScintilla();
        return _scintillaViewer.PendingEntryCount;
    }

    // ── Uncapped: Drain phase ────────────────────────────────────────────

    [BenchmarkCategory("Uncapped-Drain"), Benchmark(Baseline = true, Description = "Serilog+RTB uncap drain 50k")]
    public int SerilogUncapped_Drain()
    {
        ParallelEmitSerilog(_serilogUncapLogger);
        BenchmarkHarness.WaitForRichTextStable(_serilogUncapRtb);
        return _serilogUncapRtb.TextLength;
    }

    [BenchmarkCategory("Uncapped-Drain"), Benchmark(Description = "Scintilla uncap drain 50k")]
    public long ScintillaUncapped_Drain()
    {
        ParallelIngestScintilla();
        BenchmarkHarness.WaitForScintillaDrain(_scintillaViewer, TotalMessages);
        return _scintillaViewer.RenderedMessages;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void ParallelEmitSerilog(Serilog.ILogger logger)
    {
        var threads = new Thread[ProducerCount];
        for (var p = 0; p < ProducerCount; p++)
        {
            var start = p * MessagesPerProducer;
            var end = start + MessagesPerProducer;
            threads[p] = new Thread(() =>
            {
                for (var i = start; i < end; i++)
                    logger.Write(BenchmarkHarness.MapToSerilogLevel(_data.Levels[i]), "{Message}", _data.Messages[i]);
            });
            threads[p].Start();
        }
        foreach (var t in threads) t.Join();
    }

    private void ParallelIngestScintilla()
    {
        var threads = new Thread[ProducerCount];
        for (var p = 0; p < ProducerCount; p++)
        {
            var start = p * MessagesPerProducer;
            var end = start + MessagesPerProducer;
            threads[p] = new Thread(() =>
            {
                for (var i = start; i < end; i++)
                {
                    var bytes = _data.Utf8Messages[i];
                    _scintillaViewer.TryPost(new LogEntry
                    {
                        Level = _data.Levels[i],
                        Message = new ArraySegment<byte>(bytes, 0, bytes.Length),
                        Properties = LogEntry.EmptyProperties
                    });
                }
            });
            threads[p].Start();
        }
        foreach (var t in threads) t.Join();
    }
}
