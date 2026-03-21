# Testing

## Running Tests

```bash
dotnet test ZLogger.Scintilla.Tests/ -c Release
```

All tests run headless — no Scintilla control or UI thread required.

**~130 tests — 0 failures**

---

## Test Coverage Map

```mermaid
flowchart TB
    subgraph Pipeline["Pipeline Tests"]
        Alloc["AllocationBudgetTests<br/>Zero-alloc filter hot path"]
        Batch["BatchPreparerTests<br/>Text, styles, JSON, tokens"]
        Filter["FilterSemanticsTests<br/>Threshold model"]
        History["LogHistoryBufferTests<br/>Ring buffer + eviction"]
        Conc["ConcurrencyTests<br/>Thread safety"]
        Reg["LoggingRegistrationTests<br/>DI wiring"]
    end

    subgraph Rendering["Rendering Tests"]
        Render["RenderFormattingTests<br/>JSON highlighting + segments"]
        Prefix["LogPrefixParserTests<br/>Timestamp + level parsing"]
        Param["ParameterSpanTests<br/>Type mapping + HasParameter"]
        Url["UrlClickTests<br/>URL detection + linkifier"]
    end

    subgraph Lifecycle["Lifecycle Tests"]
        Host["HostLifecycleTests<br/>Dispose + TryPost"]
        Search["AsyncSearchTests<br/>FindNext/FindPrevious"]
    end

    subgraph Guards["Guard Tests"]
        API["PublicApiShapeTests<br/>API surface snapshot"]
        Docs["DocumentationConsistencyTests<br/>No stale references"]
        Sol["SolutionStructureTests<br/>File organization"]
    end

    subgraph Data["Data Tests"]
        Scenario["ScenarioDataFactoryTests<br/>Benchmark data determinism"]
    end
```

---

## Test Suite — 17 files

| File | Coverage Area |
|------|---------------|
| `AllocationBudgetTests` | `PreparedBatch` lifecycle, `PooledSegment<T>` return, zero-alloc filter path |
| `BatchPreparerTests` | `BatchPreparer.Prepare` — text bytes, style ranges, JSON block, token ranges |
| `FilterSemanticsTests` | `SetFilter(LogLevel)` threshold model, edge cases (Trace=All, None=None) |
| `LogHistoryBufferTests` | Enqueue, eviction at capacity, `CaptureSnapshot` with filter, cancellation |
| `ConcurrencyTests` | `_filterVersion` monotonicity under 100 concurrent `SetFilter` calls |
| `LoggingRegistrationTests` | `ScintillaLogProcessor` uses delegate, decoupled from viewer |
| `RenderFormattingTests` | `JsonSyntaxHighlighter` styling (keys, numbers, booleans, null, punctuation) |
| `LogPrefixParserTests` | Prefix parsing `[HH:mm:ss.fff LVL] [Cat]`, level → `SemanticStyle` |
| `ParameterSpanTests` | `MapClrType`, `IsLeafType`, `HasParameter` type-based lookup |
| `UrlClickTests` | URL detection, `ILinkifier` contract, click action invocation |
| `HostLifecycleTests` | Double-dispose idempotency, `TryPost` after dispose returns `false` |
| `AsyncSearchTests` | `FindNextAsync` / `FindPreviousAsync` non-blocking, cancellation |
| `PublicApiShapeTests` | Exact public member set of `ScintillaLogViewerBase` + 3 interfaces |
| `DocumentationConsistencyTests` | All docs scanned for stale type/API references |
| `SolutionStructureTests` | File/folder organization, required docs, minimum file count |
| `ScenarioDataFactoryTests` | Benchmark data factory seed reproducibility |
| `TestHelper` | Shared `MakeEntry` factory (not a test class) |

---

## Key Test Contracts

### Allocation Budget

The filter hot path must not allocate a new `List<LogEntry>` per batch. `ScintillaLogViewerBase` pre-allocates `_filterBuffer` and reuses it every drain cycle. This test acts as a CI allocation gate.

### API Shape

Asserts the exact public member set of `ScintillaLogViewerBase`, `IScintillaTheme`, `ILogColorizer`, and `ILinkifier`. Prevents accidental API surface changes.

### Documentation Consistency

Scans all docs (`docs/architecture.md`, `docs/usage.md`, `docs/benchmarks.md`, `docs/testing.md`, `README.md`) for references to deleted types and removed API patterns. Fails if any stale reference is found.

### Filter Semantics

Verifies threshold model: `LogLevel.Warning` shows Warning + Error + Critical (not just Warning). `LogLevel.Trace` = all, `LogLevel.None` = nothing.

### Concurrency

100 concurrent `SetFilter` calls — `_filterVersion` must be monotonically increasing.

### Lifecycle

- `Dispose()` called twice does not throw
- `TryPost()` after dispose returns `false` (not `ObjectDisposedException`)

---

## Adding New Tests

Tests live in `ZLogger.Scintilla.Tests/`. The project uses `xUnit`.

For tests that need a viewer without a real Scintilla control, create a `ScintillaLogViewer` instance directly — the base class handles all pipeline logic internally.
