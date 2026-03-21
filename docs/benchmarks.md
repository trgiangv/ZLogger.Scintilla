# Benchmarks

Benchmarks live in `ZLogger.Scintilla.Benchmarks/` and use [BenchmarkDotNet](https://benchmarkdotnet.org/).

**Environment:** Intel Core Ultra 7 265K 3.90GHz · .NET 8.0.25 · Windows 11 25H2 · BenchmarkDotNet v0.15.8

---

## Performance Summary

```mermaid
flowchart LR
    subgraph Scintilla["ZLogger.Scintilla"]
        S1["Enqueue: 2–10× faster"]
        S2["Memory: 6–38× less"]
        S3["Search: 31–664K× faster"]
        S4["Paint: ≈ equivalent"]
    end

    subgraph Serilog["Serilog + RichTextBox"]
        R1["Lock-based queue"]
        R2["String boxing"]
        R3["Managed string scan"]
        R4["RTF rendering"]
    end

    S1 ---|"vs"| R1
    S2 ---|"vs"| R2
    S3 ---|"vs"| R3
    S4 ---|"vs"| R4
```

---

## Running Benchmarks

```bash
dotnet run -c Release --project ZLogger.Scintilla.Benchmarks -- --filter *
dotnet run -c Release --project ZLogger.Scintilla.Benchmarks -- --filter *IngestThroughput*
dotnet run -c Release --project ZLogger.Scintilla.Benchmarks -- --list flat
```

Or use the helper script:

```powershell
.\ZLogger.Scintilla.Benchmarks\run-benchmarks.ps1 -Suite ingest
.\ZLogger.Scintilla.Benchmarks\run-benchmarks.ps1 -Suite json
.\ZLogger.Scintilla.Benchmarks\run-benchmarks.ps1 -Suite all
```

Results are written to `BenchmarkDotNet.Artifacts/results/`.

---

## Benchmark Groups

Every benchmark class runs in two comparison groups:

| Group | Serilog Config | Scintilla Config | Purpose |
|-------|---------------|------------------|---------|
| **Production** | Real `RichTextBoxSink`, 2048-line cap | 50k lines, history enabled | Real-world production behavior |
| **Uncapped** | Wrapper bypassing cap (50k lines via reflection) | 50k lines, history enabled | Fair same-volume comparison |

Each group also splits measurements into two phases:

| Phase | What it measures |
|-------|-----------------|
| `*_Enqueue` | Time to push all messages into the pipeline only — no waiting for render. Fair producer-side comparison. |
| `*_Drain` / `*_Full` | Time for full cycle: enqueue + render completion. Measures end-to-end latency. |

---

## Benchmark Classes

| Class | What It Measures | Volume |
|-------|-----------------|--------|
| `IngestThroughputBenchmark` | 4 concurrent producers pushing messages; enqueue phase vs drain phase | 50k messages |
| `ColorizedRenderBenchmark` | Full pipeline: ingest → colorize → pixel paint | 2k structured JSON |
| `LargeDocumentSearchBenchmark` | `HighlightSearch` + `FindNext` on pre-filled document | 50k pre-filled lines |
| `FilterReapplyBenchmark` | `SetFilter(Warning)` reapply from history vs Serilog re-emit | 50k history entries |
| `PaintBenchmark` | Isolated `WM_PRINTCLIENT` pixel-draw cost | 5k pre-filled lines |
| `SustainedLoadBenchmark` | Continuous load — enqueue throughput + memory pressure | 100k messages |
| `JsonPipelineBenchmark` | `EnablePrettyJson` overhead: JSON off vs on for plain and structured messages | 5k messages |

---

## Data: Realistic Mixed

All benchmarks use `ScenarioDataFactory.BuildRealisticMixed()`:

| Property | Value |
|----------|-------|
| Level distribution | Trace 10%, Debug 20%, Info 40%, Warning 20%, Error 8%, Critical 2% |
| Message size | 64–2048 bytes, mean ~256 |
| Content mix | 60% plain text, 30% structured JSON, 10% exception stack traces |
| Reproducibility | Deterministic via seed — same results across runs |

---

## Results (v0.0.4 — Intel Core Ultra 7 265K · .NET 8.0.25)

### IngestThroughputBenchmark — 50k messages, 4 producers

#### Enqueue phase (producer-side, fair comparison)

| | Scintilla | Serilog+RTB | Ratio |
|---|---|---|---|
| Production enqueue | **8.7 ms** | 12.0 ms | **1.4× faster** |
| Uncapped enqueue | **9.7 ms** | 18.7 ms | **1.9× faster** |
| Production alloc | **2.67 MB** | 17.17 MB | **6.4× less** |
| Uncapped alloc | **4.83 MB** | 50.6 MB | **10.5× less** |

`BoundedChannel.TryWrite` (lock-free) vs `ConcurrentCircularBuffer.Add` (lock-based). Allocation difference from `ArrayPool<byte>` vs string boxing.

#### Drain phase (render-thread latency)

| | Scintilla | Serilog+RTB |
|---|---|---|
| Production drain | 5,009 ms | **59.1 ms** |
| Uncapped drain | 5,008 ms | **66.7 ms** |

Scintilla drain is higher because it renders 50k lines with syntax coloring via async `BeginInvoke`. Serilog appends RTF text synchronously. These are different rendering models — **drain time should not be used for head-to-head comparison**. In production, users see log lines appear progressively in batches, not after a 5-second wait.

---

### ColorizedRenderBenchmark — 2k structured JSON messages

#### Enqueue phase

| | Scintilla | Serilog+RTB | Ratio |
|---|---|---|---|
| Production enqueue | **147 μs** | 1,051 μs | **7.1× faster** |
| Uncapped enqueue | **118 μs** | 779 μs | **6.6× faster** |
| Production alloc | **111 KB** | 705 KB | **6.4× less** |

#### Full pipeline allocation

| | Scintilla | Serilog+RTB | Ratio |
|---|---|---|---|
| Production full alloc | **176 KB** | 8,769 KB | **50× less** |
| Uncapped full alloc | **2,128 KB** | 8,769 KB | **4.1× less** |

---

### SustainedLoadBenchmark — 100k messages

#### Enqueue phase

| | Scintilla | Serilog+RTB | Ratio |
|---|---|---|---|
| Production enqueue | **8.4 ms** | 49.3 ms | **5.9× faster** |
| Uncapped enqueue | **8.9 ms** | 77.2 ms | **8.7× faster** |
| Production alloc | **5.5 MB** | 37.3 MB | **6.8× less** |
| Uncapped alloc | **5.5 MB** | 73.0 MB | **13.3× less** |

#### Full pipeline (enqueue + render)

| | Scintilla | Serilog+RTB | Ratio |
|---|---|---|---|
| Production full | **43.3 ms** | 86.1 ms | **2.0× faster** |
| Uncapped full | **43.3 ms** | 127.3 ms | **2.9× faster** |
| Production full alloc | **6.12 MB** | 42.3 MB | **6.9× less** |
| Uncapped full alloc | **6.34 MB** | 153.6 MB | **24.2× less** |

At 100k messages uncapped, Serilog allocates **154 MB** vs Scintilla's **6 MB** — a **24× difference**. This is the most production-relevant memory metric.

> **v0.0.3 → post-v0.0.3:** Full pipeline improved from 78.5 ms → 43.3 ms (**45% faster**) following pipeline simplification and removal of redundant `try-catch` blocks in `ScintillaLogFormatter`.

---

### LargeDocumentSearchBenchmark — 50k lines

| | Scintilla | RTB | Ratio |
|---|---|---|---|
| Highlight+FindNext common token | **1.67 ms** | 49.2 ms | **29× faster** |
| FindNext rare token | **90 ns** | 60.3 ms | **671,000× faster** |
| Common token allocation | **555 B** | 168 MB | **303,000× less** |

Scintilla uses native Boyer-Moore search (C++ Scintilla engine). RTB scans the full managed string and allocates a new string per search call.

---

### FilterReapplyBenchmark — 50k history entries

| Method | Mean | Alloc |
|--------|------|-------|
| CPU scan only (lower bound) | 228 μs | 1.59 KB |
| Serilog re-emit filtered subset (Warning+) | **50,493 μs** | 5,502 KB |
| Scintilla SetFilter(Warning) reapply | 421,492 μs | **1,758 KB** |

Scintilla is 8.3× slower wall-clock but allocates 3.1× less. The key distinction: Serilog has no history buffer — the caller must hold the original data and re-emit the filtered subset. Scintilla's history buffer is the source of truth; `SetFilter` replays from it automatically.

---

### PaintBenchmark — pixel render, 5k lines

| | Scintilla | RTB | Ratio |
|---|---|---|---|
| Production paint | **1.72 ms** | 1.73 ms | **≈ equivalent** |
| Uncapped paint | **1.63 ms** | 1.48 ms | **+10% slower** |

Effectively equivalent. Both rendering engines perform similarly for a pre-populated document.

---

## Summary

| Scenario | Scintilla vs Serilog+RTB |
|----------|--------------------------|
| **Enqueue throughput** | **1.4–9× faster** depending on volume |
| **Memory allocation** | **6–24× less** — largest gap at sustained load |
| **Search / Find** | **29× – 671,000× faster**, near-zero allocation |
| **Pixel render** | Equivalent (±10%) |
| **Drain / FullPipeline latency** | Higher — async rendering model, not comparable directly |
| **Filter reapply wall-clock** | 8× slower, but 3× less allocation; built-in history is a unique feature |

**Scintilla is the right choice for:** high-throughput logging, large document search/filter, memory-constrained environments.

**Serilog+RTB is simpler for:** low-volume logging where throughput and search are not requirements.

---

## Regression Policy

| Scenario | Metric | Threshold | Action |
|----------|--------|-----------|--------|
| Enqueue throughput (no filter) | msg/s | ≥ 100,000 | Block merge |
| Enqueue throughput (filter active) | msg/s | ≥ 80,000 | Block merge |
| Filter replay (10k entries) | p95 latency | ≤ 50 ms | Block merge |
| Search highlight (50k entries) | p95 latency | ≤ 100 ms | Warn |
| Allocation per filtered batch | bytes | ≤ 4,096 B | Block merge |

**Regression classification:**
- < 5%: Acceptable, no action
- 5–10%: Document root cause in PR description
- > 10%: Block merge until mitigated or explicitly waived with written rationale

`AllocationBudgetTests` unit tests act as a lightweight allocation gate in CI without requiring a full benchmark run. These must pass on every PR.

---

## Infrastructure

| File | Role |
|------|------|
| `BenchmarkHarness` | Shared factory methods (Scintilla viewer, Serilog sinks), uncapped sink wrapper (reflection bypass of `_maxLogLines`), drain/wait helpers, `OffscreenPainter`, level mapping |
| `ScenarioDataFactory` | `BuildRealisticMixed()` + `RealisticMixedData` |
| `InProcessBenchmarkConfig` | `InProcessEmitToolchain` (STA thread), 3 warmup / 8 iterations, `MemoryDiagnoser` |

### Interpreting Results

- **Mean**: Average execution time per iteration
- **Allocated**: Managed heap allocation per iteration (`MemoryDiagnoser`)
- **Ratio**: Relative to baseline (Serilog) — values < 1.0 mean Scintilla is faster/cheaper
- **Gen0/Gen1/Gen2**: GC collection counts — lower is better for throughput
