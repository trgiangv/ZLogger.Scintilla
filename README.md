# ZLogger.Scintilla

High-throughput, syntax-highlighted log viewer control for WinForms and WPF, built on [Scintilla.NET](https://github.com/VPKSoft/Scintilla.NET) and [ZLogger](https://github.com/Cysharp/ZLogger).

## Features

- Zero-allocation render pipeline (`ArrayPool<byte>`, pre-allocated filter buffer)
- Structured payload pretty-printing with inline JSON blocks
- Clickable URL detection and custom linkifier support (`ILinkifier`)
- Per-token colorization via `ILogColorizer`
- Real-time **threshold** level filtering — `SetFilter(LogLevel.Warning)` shows Warning/Error/Critical
- Find Next / Find Previous with match counter, async-first navigation
- Dark and light themes (`IScintillaTheme`), line numbers toggle
- `IHost` / `HostBuilder` integration for WPF, WinForms, and Revit add-ins

## Quick Start

```csharp
// WinForms — viewer is created inside AddZLoggerScintillaWinForms
services.AddLogging(logging =>
    logging.AddZLoggerScintillaWinForms(
        v => v
            .SetTheme(ScintillaThemes.Dark)
            .WithColorized(new MyColorizer())
            .WithLinkify(new MyLinkifier())
    )
);

// Retrieve from DI
var viewer = host.Services.GetRequiredService<ScintillaLogViewer>();
panel.Controls.Add(viewer.ScintillaControl);
viewer.Start();

// Runtime control
viewer.SetFilter(LogLevel.Warning);
viewer.HighlightSearch("error");
var pos = await viewer.FindNextAsync();
viewer.SetTheme(ScintillaThemes.Light);
viewer.SetPrettyJson(true);
viewer.SetLineNumbers(true);
viewer.Clear();
```

```csharp
// WPF
services.AddLogging(logging =>
    logging.AddZLoggerScintillaWpf(
        v => v.SetTheme(ScintillaThemes.Dark)
    )
);

var viewer = host.Services.GetRequiredService<ScintillaLogViewerWpf>();
hostGrid.Children.Add((UIElement)viewer.HostElement);
```

## Documentation

| Document | Description |
|----------|-------------|
| [docs/architecture.md](docs/architecture.md) | Component map, pipeline flow, thread model (mermaid diagrams) |
| [docs/usage.md](docs/usage.md) | Registration, runtime API, builder options, extension points |
| [docs/testing.md](docs/testing.md) | Test suite overview, contracts, coverage map |
| [docs/benchmarks.md](docs/benchmarks.md) | Benchmark results, analysis, regression policy |

## Extension Points

Only 3 interfaces for user implementation:

| Interface | Purpose |
|-----------|---------|
| `IScintillaTheme` | Custom color theme (18 properties) |
| `ILogColorizer` | Per-token foreground color override |
| `ILinkifier` | Clickable token detection |

## Test Suite

**116 tests — 0 failures**

```bash
dotnet test ZLogger.Scintilla.Tests/ -c Release
```

## Benchmarks

Key results vs Serilog+RichTextBox (Intel Core Ultra 7 265K · .NET 8.0.25):

| Scenario | Result |
|----------|--------|
| Enqueue throughput (50k, 4 producers) | **2–8× faster** |
| Memory allocation (sustained 100k) | **3–13× less** |
| Search / FindNext (50k lines) | **32× – 662,000× faster** |
| Pixel render | Equivalent (±4%) |

```bash
dotnet run -c Release --project ZLogger.Scintilla.Benchmarks -- --filter *
```

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

> **Note:** `LookupEngine` is included as a git submodule and merged into `ZLogger.Scintilla.dll` at build time using ILRepack. The NuGet package ships a single assembly with no external LookupEngine dependency.
