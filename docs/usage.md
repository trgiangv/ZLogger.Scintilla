# Usage Guide

## Registration Flow

```mermaid
flowchart LR
    subgraph DI["DI Registration (startup)"]
        Ext["AddZLoggerScintillaWinForms<br/>or AddZLoggerScintillaWpf"]
        Viewer["ScintillaLogViewer<br/>(created internally)"]
        Proc["ScintillaLogProcessor<br/>(ZLogger sink)"]
        Fmt["ScintillaLogFormatter"]
    end

    Ext -->|"new"| Viewer
    Ext -->|"new"| Proc
    Proc -->|"UseFormatter"| Fmt
    Proc -->|"TryPost delegate"| Viewer

    subgraph Resolve["Runtime (GetRequiredService)"]
        Base["ScintillaLogViewerBase"]
        Concrete["ScintillaLogViewer<br/>or ScintillaLogViewerWpf"]
    end

    Viewer -.->|"registered as"| Base
    Viewer -.->|"registered as"| Concrete
```

### WinForms

```csharp
services.AddLogging(logging =>
    logging.AddZLoggerScintillaWinForms(
        v => v
            .Display(maxLines: 50_000, font: "Cascadia Mono", fontSize: 10)
            .SetTheme(ScintillaThemes.Dark)
            .WithColorized(new MyColorizer())
            .WithLinkify(new MyLinkifier())
    )
);

var viewer = host.Services.GetRequiredService<ScintillaLogViewer>();
panel.Controls.Add(viewer.ScintillaControl);
viewer.Start();
```

### WPF

```csharp
services.AddLogging(logging =>
    logging.AddZLoggerScintillaWpf(
        v => v.SetTheme(ScintillaThemes.Dark)
    )
);

var viewer = host.Services.GetRequiredService<ScintillaLogViewerWpf>();
hostGrid.Children.Add((UIElement)viewer.HostElement);
viewer.Start();
```

### Platform-agnostic

```csharp
var viewer = host.Services.GetRequiredService<ScintillaLogViewerBase>();
```

---

## Lifecycle

```mermaid
stateDiagram-v2
    [*] --> Created: new via DI
    Created --> Running: Start()
    Running --> Stopped: Stop()
    Stopped --> Running: Start()
    Running --> Disposed: Dispose()
    Stopped --> Disposed: Dispose()
    Disposed --> Disposed: Dispose() (idempotent)
    
    note right of Running
        Pump thread active
        Accepting log entries
    end note
    
    note right of Disposed
        TryPost() returns false
        No exceptions thrown
    end note
```

---

## Runtime API

```mermaid
flowchart TB
    subgraph Lifecycle
        Start["Start()"]
        Stop["Stop()"]
        Clear["Clear()"]
        Dispose["Dispose()"]
    end

    subgraph Display
        Theme["SetTheme(IScintillaTheme)"]
        Json["SetPrettyJson(bool)"]
        Lines["SetLineNumbers(bool)"]
    end

    subgraph Filter
        SetF["SetFilter(LogLevel)"]
    end

    subgraph Search
        HL["HighlightSearch(pattern, useRegex)"]
        FN["FindNextAsync(ct)"]
        FP["FindPreviousAsync(ct)"]
        CM["CountMatchesAsync(ct)"]
        CS["ClearSearch()"]
    end

    subgraph Metrics
        RM["RenderedMessages"]
        DM["DroppedMessages"]
        PE["PendingEntryCount"]
        HE["HistoryEntries"]
    end
```

### Filter — Threshold Model

```mermaid
flowchart LR
    T["Trace"] --> D["Debug"] --> I["Information"] --> W["Warning"] --> E["Error"] --> C["Critical"]
```

`SetFilter(LogLevel.Warning)` → shows Warning + Error + Critical (everything ≥ Warning).

| Call | Entries shown |
|------|---------------|
| `SetFilter(LogLevel.Trace)` | All |
| `SetFilter(LogLevel.Debug)` | Debug → Critical |
| `SetFilter(LogLevel.Warning)` | Warning → Critical |
| `SetFilter(LogLevel.None)` | Nothing |

### Search

```csharp
viewer.HighlightSearch("error");                    // highlight all matches
viewer.HighlightSearch(@"\bERR\b", useRegex: true); // regex support
var pos = await viewer.FindNextAsync();              // SearchPosition { Current, Total }
var pos = await viewer.FindPreviousAsync();
int count = await viewer.CountMatchesAsync();
viewer.ClearSearch();
```

### Metrics

```csharp
long rendered = viewer.RenderedMessages;  // total entries rendered to UI
long dropped  = viewer.DroppedMessages;   // entries dropped by backpressure
long pending  = viewer.PendingEntryCount; // entries waiting in channel
long history  = viewer.HistoryEntries;    // entries in history buffer
```

---

## Builder Options

`ScintillaOptions` — fluent configuration at startup (immutable after construction):

| Method | Default | Description |
|--------|---------|-------------|
| `.Channel(capacity, flushMs, maxBatch, dropPolicy)` | 20000 / 75ms / 500 / DropWrite | Ingest channel tuning |
| `.Display(maxLines, font, fontSize, autoScroll)` | 50000 / Cascadia Mono / 10 / true | Display limits |
| `.History(maxEntries, disabled)` | 50000 / false | History buffer for filter reapply |
| `.SetTheme(IScintillaTheme)` | `ScintillaThemes.Dark` | Initial color theme |
| `.WithColorized(ILogColorizer)` | null | Token-level color override |
| `.WithLinkify(ILinkifier)` | null | Clickable token handler |
| `.WithEnrichmentErrorSink(Action<Exception>)` | null | Sink for colorizer/linkifier errors |

---

## Extension Points

```mermaid
flowchart LR
    subgraph UserImplements["User implements"]
        IT["IScintillaTheme<br/>18 color properties"]
        IC["ILogColorizer<br/>TryGetColor(utf8Token)"]
        IL["ILinkifier<br/>TryMatch(utf8Token, context)"]
    end

    subgraph Internal["Internal pipeline"]
        TRE["TokenResolutionEngine"]
        Style["ScintillaStyleService"]
    end

    IL -->|"first match wins"| TRE
    IC -->|"color override"| TRE
    IT -->|"ApplyThemeStyles"| Style
```

### IScintillaTheme

Custom color theme — implement all 18 color properties. See `ScintillaThemes.Dark` / `ScintillaThemes.Light` for reference.

### ILogColorizer

Override token foreground colors. Called on the **pump thread** — must be thread-safe.

See `Public/ILogColorizer.cs` for the contract signature.

### ILinkifier

Make tokens clickable. Receives `RenderContext` with typed parameter metadata via `ParameterSpan.HasParameter(context, clrTypeFullName, formattedValue)`. Called on the **pump thread** — must be thread-safe.

See `Public/ILinkifier.cs` for the contract signature.

---

## IHost Integration

```mermaid
sequenceDiagram
    participant Builder as Host.CreateApplicationBuilder
    participant DI as IServiceProvider
    participant Viewer as ScintillaLogViewer
    participant Form as WinForms / WPF

    Builder->>DI: AddZLoggerScintillaWinForms(configure)
    Note over DI: Viewer + Processor created
    DI-->>Form: GetRequiredService<ScintillaLogViewer>()
    Form->>Viewer: panel.Controls.Add(ScintillaControl)
    Form->>Viewer: Start()
    Note over Viewer: Pump thread running
    Form->>Viewer: Stop() + Dispose()
```

---

## Target Frameworks

- `net48` — WinForms / WPF legacy
- `net8.0-windows` — WinForms / WPF modern

## Dependencies

| Package | Purpose |
|---------|---------|
| `Scintilla5.NET` | Editor engine |
| `ZLogger` | Structured logging source |
| `Microsoft.Extensions.Logging` | `ILogger` / `IHost` integration |
| `System.Threading.Channels` | Bounded ingest queue |
| [LookupEngine](https://github.com/lookup-foundation/LookupEngine) | Object decomposition for pretty JSON (bundled via ILRepack) |

> **Note:** `LookupEngine` is included as a git submodule under `external/LookupEngine` and merged into `ZLogger.Scintilla.dll` at build time using ILRepack. The NuGet package ships a single assembly with no external LookupEngine dependency.
