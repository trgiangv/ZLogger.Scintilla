# Architecture

ZLogger.Scintilla is a high-throughput, in-process log viewer built on [Scintilla.NET](https://github.com/VPKSoft/Scintilla.NET) and [ZLogger](https://github.com/Cysharp/ZLogger). It renders structured log entries with semantic syntax highlighting, clickable links, JSON pretty-printing, and real-time search/filter — without blocking the logging thread.

## Design Principles

- **Prepare off-thread, apply on-thread** — formatting, segment building, and style computation happen on the pump thread; the UI thread only calls Scintilla APIs
- **Zero shared mutable state** — `PreparedBatch` is an immutable transfer object; no `List<T>` crosses thread boundaries
- **ArrayPool everywhere** — `byte[]`, `StyleRange[]`, `TokenRange[]` are rented and returned after UI application
- **Narrow public surface** — only 3 interfaces for user implementation: `IScintillaTheme`, `ILogColorizer`, `ILinkifier`

---

## Component Map

```mermaid
graph TB
    subgraph Public["Public/ — User-facing API"]
        Base["ScintillaLogViewerBase<br/><i>abstract base class</i>"]
        WF["ScintillaLogViewer<br/><i>WinForms host</i>"]
        WPF["ScintillaLogViewerWpf<br/><i>WPF host</i>"]
        Opts["ScintillaOptions<br/><i>fluent config</i>"]
        Ext["LoggingBuilderExtensions<br/><i>DI registration</i>"]
        IT["IScintillaTheme"]
        IC["ILogColorizer"]
        IL["ILinkifier"]
    end

    subgraph Pipeline["Pipeline/ — Log entry lifecycle"]
        Proc["ScintillaLogProcessor<br/><i>ZLogger IAsyncLogProcessor</i>"]
        Pump["LogIngestPump<br/><i>BoundedChannel</i>"]
        Hist["LogHistoryBuffer<br/><i>circular buffer</i>"]
        Prep["BatchPreparer<br/><i>entries → PreparedBatch</i>"]
        Search["SearchCoordinator<br/><i>async find/highlight</i>"]
        Metrics["LogIngestMetrics"]
    end

    subgraph Rendering["Rendering/ — LogEntry → styled segments"]
        LR["LogRenderer"]
        DVF["DisplayValueFormatter"]
        JSH["JsonSyntaxHighlighter"]
        TRE["TokenResolutionEngine"]
        LPP["LogPrefixParser"]
        FMT["ScintillaLogFormatter<br/><i>IZLoggerFormatter</i>"]
        URL["UrlScanner"]
    end

    subgraph Services["Services/ — Scintilla.NET control wrappers"]
        Doc["ScintillaDocument"]
        Style["ScintillaStyleService"]
        SS["ScintillaSearchService"]
        Inter["ScintillaInteractionService"]
        SM["StyleMapper"]
    end

    WF --> Base
    WPF --> Base
    Ext --> Base
    Ext --> Proc
    Base --> Pump
    Base --> Hist
    Base --> Prep
    Base --> Search
    Base --> Doc
    Proc --> FMT
    Prep --> LR
    LR --> DVF
    LR --> JSH
    DVF --> TRE
    DVF --> URL
    LR --> LPP
    TRE --> IC
    TRE --> IL
    Doc --> Style
    Doc --> SS
    Doc --> Inter
    Style --> SM
```

---

## Log Entry Pipeline

```mermaid
sequenceDiagram
    participant App as User Code
    participant Proc as ScintillaLogProcessor
    participant Chan as BoundedChannel
    participant Pump as Pump Thread
    participant Hist as LogHistoryBuffer
    participant Prep as BatchPreparer
    participant Queue as ConcurrentQueue
    participant UI as UI Thread

    App->>Proc: logger.LogInformation(...)
    Proc->>Proc: Format UTF-8 prefix<br/>Extract ParameterSpans<br/>Build JSON bytes (if enabled)
    Proc->>Chan: TryPost(LogEntry)

    loop Every 75ms (FlushIntervalMs)
        Pump->>Chan: TryReadBatch(max 500)
        Pump->>Hist: Enqueue entries
        Pump->>Pump: Filter: entry.Level ≥ minimumLevel
        Pump->>Prep: Prepare(visibleEntries)
        Note over Prep: LogRenderer.BuildSegments<br/>StyleMapper.AppendStyleRanges<br/>Encode UTF-8 → pooled byte[]<br/>Collect TokenRanges<br/>→ PreparedBatch (immutable)
        Prep-->>Pump: PreparedBatch
        Pump->>Queue: Enqueue(PreparedBatch)
        Pump->>UI: BeginInvoke(DrainRenderQueue)
    end

    UI->>Queue: TryDequeue
    UI->>UI: ScintillaDocument.ApplyPreparedBatch<br/>SCI_APPENDTEXT + SCI_SETSTYLINGEX<br/>Register TokenRanges<br/>TrimDocumentIfNeeded
    UI->>UI: batch.ReturnBuffers() → ArrayPool<br/>batch.ReleaseEntryBuffers()
    UI->>UI: RefreshSearchHighlight (once per drain)
```

---

## Rendering Pipeline

```mermaid
flowchart LR
    Entry["LogEntry"] --> LPP["LogPrefixParser<br/>parse timestamp + level"]
    Entry --> DVF["DisplayValueFormatter<br/>parameter spans + tokens"]
    Entry --> JSH["JsonSyntaxHighlighter<br/>JSON syntax coloring"]

    DVF --> TRE["TokenResolutionEngine"]
    DVF --> URL["UrlScanner<br/>clickable URLs"]

    TRE --> IC["ILogColorizer"]
    TRE --> IL["ILinkifier"]

    LPP --> Seg["RenderSegment[]"]
    DVF --> Seg
    JSH --> Seg

    Seg --> SM["StyleMapper"]
    SM --> SR["StyleRange[] + TokenRange[]"]
    SR --> Batch["PreparedBatch<br/>(pooled arrays)"]
```

---

## Thread Ownership

```mermaid
flowchart LR
    subgraph PumpThread["Pump Thread"]
        BB["_batchBuffer"]
        BP["BatchPreparer"]
        LR["LogRenderer"]
        FB["_filterBuffer"]
    end

    subgraph Transfer["Transfer (immutable)"]
        PB["PreparedBatch"]
    end

    subgraph UIThread["UI Thread"]
        SC["Scintilla Control"]
        Doc["ScintillaDocument"]
        SS["ScintillaStyleService"]
    end

    PumpThread -->|"ConcurrentQueue"| Transfer
    Transfer -->|"BeginInvoke"| UIThread
```

| Data | Owner | Notes |
|------|-------|-------|
| `_batchBuffer`, `_filterBuffer` | Pump thread | Never crosses boundary |
| `BatchPreparer` scratch buffers | Pump thread | Ephemeral per replay |
| `PreparedBatch` | Transfer | Created on pump, consumed on UI, returned to pool |
| Scintilla control | UI thread | All `SCI_*` calls happen here |
| `ILogColorizer` / `ILinkifier` | Pump thread | Implementations **must** be thread-safe |

---

## Filter Flow

```mermaid
sequenceDiagram
    participant User
    participant Base as ScintillaLogViewerBase
    participant Hist as LogHistoryBuffer
    participant Prep as BatchPreparer (ephemeral)
    participant Doc as ScintillaDocument

    User->>Base: SetFilter(LogLevel.Warning)
    Base->>Base: Cancel previous filter task
    Base->>Base: Increment _filterVersion
    Base->>Doc: Clear()

    par Background Thread
        Base->>Hist: CaptureChunkedSnapshot(Warning)
        loop Per chunk (1000 entries)
            Hist-->>Base: List<LogEntry> chunk
            Base->>Prep: Prepare(chunk)
            Prep-->>Base: PreparedBatch
            Base->>Doc: BeginInvoke(ApplyPreparedBatch)
        end
    end
```

**Threshold semantics:** `entry.Level >= minimumLevel`

| `SetFilter(level)` | Entries shown |
|---------------------|---------------|
| `LogLevel.Trace` | All |
| `LogLevel.Warning` | Warning, Error, Critical |
| `LogLevel.None` | Nothing |

---

## Search Flow

```mermaid
sequenceDiagram
    participant User
    participant Coord as SearchCoordinator
    participant Doc as ScintillaDocument
    participant SS as ScintillaSearchService

    User->>Coord: HighlightSearch("error")
    Coord->>Doc: BeginInvoke
    Doc->>SS: SetActiveSearch + HighlightVisibleMatches

    User->>Coord: FindNextAsync()
    Coord->>Doc: dispatchAsync
    Doc->>SS: FindNext (Boyer-Moore)
    SS-->>Coord: SearchMatch { Found, CurrentIndex }
    Coord->>Doc: BuildMatchIndex (O(log n) cache)
    Coord-->>User: SearchPosition { Current, Total }
```

Search refresh is lazy: `RefreshHighlightIfActive` runs once per `DrainRenderQueue` cycle, not per batch.

---

## Project Structure

```
ZLogger.Scintilla/
├── Models/          Data structs, value types, constants
├── Public/          User-facing API (base class, interfaces, DI, themes, options)
├── Pipeline/        Log lifecycle: ingest → filter → prepare → UI dispatch
├── Rendering/       LogEntry → styled byte segments (formatter, highlighters, parsers)
└── Services/        Direct Scintilla.NET control wrappers
```

### Models/ — 13 files

| File | Role |
|------|------|
| `LogEntry` | In-memory log entry with pooled `byte[]` |
| `PreparedBatch` + `PooledSegment<T>` | Immutable transfer: text + styles + tokens (pooled) |
| `RenderContext` | Message bytes + properties for `ILinkifier`/`ILogColorizer` |
| `RenderSegment` | `{ Utf8Length, SemanticStyle, TokenPayload, IsLink }` |
| `SemanticStyle` | Enum + style ID mapping |
| `StyleRange`, `TokenRange`, `TokenPayload` | Scintilla styling primitives |
| `ParameterSpan` | Typed parameter metadata + `IsLeafType()` classification |
| `ParsedPrefix`, `SearchPosition`, `SearchMatch` | Small value types |
| `LogConstants` | SCI message IDs, capacities, property keys |

### Public/ — 9 files

| File | Role |
|------|------|
| `ScintillaLogViewerBase` | Abstract base — pump loop, filter, history, search, metrics |
| `ScintillaLogViewer` | WinForms `UserControl` host |
| `ScintillaLogViewerWpf` | WPF `WindowsFormsHost` wrapper |
| `ScintillaOptions` | Fluent startup configuration |
| `LoggingBuilderExtensions` | `AddZLoggerScintillaWinForms` / `Wpf` |
| `IScintillaTheme`, `ILogColorizer`, `ILinkifier` | Extension point contracts |
| `ScintillaThemes` | Built-in Dark / Light themes |

### Pipeline/ — 6 files

| File | Role |
|------|------|
| `ScintillaLogProcessor` | ZLogger `IAsyncLogProcessor` entry point |
| `LogIngestPump` | `BoundedChannel<LogEntry>` producer/consumer |
| `LogHistoryBuffer` | Circular buffer; chunked snapshot for filter reapply |
| `BatchPreparer` | Off-thread: entries → `PreparedBatch` (pooled arrays) |
| `SearchCoordinator` | Async find/highlight dispatch |
| `LogIngestMetrics` | Rendered / dropped / backlog counters |

### Rendering/ — 9 files

| File | Role |
|------|------|
| `LogRenderer` | Orchestrator: `BuildSegments`, `BuildJsonSegments`, `ApplyThemeStyles` |
| `ScintillaLogFormatter` | `IZLoggerFormatter`: `[HH:mm:ss.fff LVL] [Cat] {msg}` |
| `DisplayValueFormatter` | Parameter span styling + linkifier/colorizer per token |
| `JsonSyntaxHighlighter` | JSON syntax coloring (no serialization) |
| `TokenResolutionEngine` | Try `ILinkifier` then `ILogColorizer` per token |
| `LogPrefixParser` | Parse `[HH:mm:ss.fff LVL] [Cat]` prefix |
| `UrlScanner` | URL/email detection + linkification |
| `RenderingChars` | Shared char sets and classification helpers |
| `RenderTokenWriter` | Segment writer helper |

### Services/ — 6 files

| File | Role |
|------|------|
| `ScintillaDocument` | UI thread: apply `PreparedBatch` → Scintilla |
| `ScintillaStyleService` | `SCI_SETSTYLINGEX`, dynamic style allocation |
| `ScintillaSearchService` | Find, highlight, match position cache |
| `ScintillaInteractionService` | Click/hover → invoke link `Action` |
| `StyleMapper` | `RenderSegment[]` → `StyleRange[]` |
| `NativeWindowsHelper` | Win32 dark mode |
