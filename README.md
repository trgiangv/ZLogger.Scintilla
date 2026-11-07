# ZLogger.Scintilla

A log viewer control for WinForms and WPF. Drop it into a window, register it with `ILogger`, and your [ZLogger](https://github.com/Cysharp/ZLogger) output appears color-coded, filterable, searchable and clickable. Built on the [Scintilla](https://github.com/VPKSoft/Scintilla.NET) editor engine.

## Features

- **Readable at a glance** — timestamp, level and category in their own colors, and JSON payloads with syntax highlighting
- **Structured values as JSON** — objects passed as log parameters can be shown as pretty-printed JSON blocks. Toggle it at runtime and the lines already shown are re-rendered
- **Level filter** — `SetFilter(LogLevel.Warning)` shows Warning, Error and Critical, including lines that were logged before the filter changed
- **Search** — highlight every match, plain text or regular expression, step with Find Next / Find Previous, and get the position ("match 3 of 120")
- **Clickable links** — `http` and `https` URLs open in the browser. Make your own tokens clickable, such as element IDs, with `ILinkifier`
- **Your own colors** — color specific tokens with `ILogColorizer`, or supply a full theme. Dark and light themes are built in
- **Stays out of the way** — auto-scroll follows new lines and stops while you scroll back to read. With the default settings a log call never waits for the UI: when the viewer cannot keep up, extra messages are dropped and counted
- **One-line setup** — `AddZLoggerScintillaWinForms()` or `AddZLoggerScintillaWpf()` on `ILoggingBuilder`, with `IHost` support. Targets `net48`, `net8.0-windows` and `net10.0-windows`

## Quick Start

```csharp
// WinForms
services.AddLogging(logging =>
    logging.AddZLoggerScintillaWinForms(
        v => v
            .SetTheme(ScintillaThemes.Dark)
            .WithColorized(new MyColorizer())
            .WithLinkify(new MyLinkifier())
    )
);

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
viewer.Start();
```

## Performance

Compared with Serilog and its colored RichTextBox sink (`Serilog.Sinks.RichTextBox.WinForms.Colored`), both keeping 2,048 lines, in the same window and with the same messages. Real on-screen rendering on .NET 8, median of 3 runs on one machine.

| 50,000 messages from 4 threads | ZLogger.Scintilla | Serilog sink |
|--------------------------------|-------------------|--------------|
| Last line on screen | **54 ms** | 123 ms |
| Longest time the window could not repaint | **21 ms** | 54 ms |
| Repaints per second during the burst | **50** | 16 |
| Search the whole log afterwards | **0.5 ms** | 12 ms |
| Memory allocated | **15 MB** | 43 MB |

| 20,000 messages/s for 5 s | ZLogger.Scintilla | Serilog sink |
|---------------------------|-------------------|--------------|
| Longest time the window could not repaint | **30 ms** | 55 ms |
| Repaints per second | **62** | 13 |
| Memory allocated | **91 MB** | 764 MB |
| Memory held | 29 MB | **18 MB** |

- **50,000 lines by default**, which the sink cannot be configured to keep. At that size, searching the whole log takes 12 ms and Page Up 2.6 ms.
- **On the logging thread**, a `ZLogInformation` call with immutable values costs about 150 ns and allocates nothing, about what ZLogger alone costs (BenchmarkDotNet, before the queue write).
- **The editor itself**, against RichTextBox at 50,000 lines: filled 4.5x faster (0.55 s vs 2.5 s), with 10x less memory (43 MB vs 426 MB), and searched more than 20x faster (11 ms vs 250+ ms).

Method, every metric, and the limits of these numbers: [docs/benchmarks.md](docs/benchmarks.md).

```bash
dotnet run -c Release --project ZLogger.Scintilla.Benchmarks -- all
```

## Extension Points

| Interface | Purpose |
|-----------|---------|
| `IScintillaTheme` | Custom color theme (18 colors) |
| `ILogColorizer` | Color specific tokens |
| `ILinkifier` | Make tokens clickable, with your own click action |

## Documentation

| Document | Description |
|----------|-------------|
| [docs/usage.md](docs/usage.md) | Registration, runtime API, options, extension points |
| [docs/benchmarks.md](docs/benchmarks.md) | Benchmark method, results, limits |
| [docs/known-issues.md](docs/known-issues.md) | Known limits |
| [docs/architecture.md](docs/architecture.md) | How the pipeline and threads work |
| [docs/testing.md](docs/testing.md) | Test suite |

## Tests

198 tests, run on both `net48` and `net8.0-windows`. Rendering, scrolling, search and clicks are tested in real windows with a running message loop.

```bash
dotnet test --project ZLogger.Scintilla.Tests -c Release
```

## Dependencies

| Package | Purpose |
|---------|---------|
| `Scintilla5.NET` | Editor control |
| `ZLogger` | Logging |
| `Microsoft.Extensions.Logging` | `ILogger` and `IHost` integration |
| `System.Threading.Channels`, `System.Text.Json` | `net48` only; built into the newer targets |

Pretty JSON uses [LookupEngine](https://github.com/lookup-foundation/LookupEngine), compiled into `ZLogger.Scintilla.dll`, so the package adds no separate dependency for it.
