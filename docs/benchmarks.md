# Benchmarks

Numbers published before this page are **withdrawn**. That suite ran without a message loop, so neither control ever displayed anything.

The suite answers three separate questions, so that a result can be attributed to the layer that caused it:

| Suite | Question | UI | Time |
|-------|----------|----|------|
| `logger` | ZLogger vs Serilog, and what this repo's processor adds | None | ~1 min |
| `control` | Scintilla vs RichTextBox, with no logging framework | Visible window | ~1 min |
| `stack` | This library vs Serilog + `Serilog.Sinks.RichTextBox.WinForms.Colored`, end to end, as a user experiences it | Visible window | ~2 min |

```bash
dotnet run -c Release --project ZLogger.Scintilla.Benchmarks -- logger
dotnet run -c Release --project ZLogger.Scintilla.Benchmarks -- control
dotnet run -c Release --project ZLogger.Scintilla.Benchmarks -- stack
dotnet run -c Release --project ZLogger.Scintilla.Benchmarks -- all
```

`control` and `stack` accept a repeat count, for example `-- control 5`. The default is 3. Each run uses a fresh process.

**Environment:** Intel Core Ultra 7 265K, 63.5 GB, Windows build 26300, .NET 8.0, 1200x800 window. Scintilla5.NET 7.1.1 (native Scintilla 5.6.7), ZLogger 2.6.1, Serilog 4.4.0, sink 3.2.1, BenchmarkDotNet 0.15.8.

---

## Stack: end to end, at the same size

The sink's public API allows at most 2,048 lines, and it renders its whole buffer again on every flush, so its cost depends directly on how many lines it keeps. Comparing it at any other size than the library says nothing, so both keep **2,048 lines** here, with the same window, font, word wrap, log template and messages. Median of 3 runs.

A difference here belongs to framework, pipeline and control together, not to any one of them.

**Burst: 50,000 messages from 4 threads**

| What the user experiences | This library | Serilog sink |
|---------------------------|--------------|--------------|
| A single line appears, when idle | 16 ms | 16 ms |
| Last line on screen | **54 ms** | 123 ms |
| Logging threads held up for | **39 ms** | 43 ms |
| Longest time the window could not paint | **21 ms** | 54 ms |
| Paints per second during the load | **50** | 16 |
| Find a token that is not there, afterwards | **0.5 ms** | 12 ms |
| Page up, afterwards | **2.5 ms** | 11 ms |
| Total allocation | **15 MB** | 43 MB |
| Private memory over baseline | **19 MB** | 25 MB |

**Sustained: 20,000 messages/s for 5 s from 1 thread**

| What the user experiences | This library | Serilog sink |
|---------------------------|--------------|--------------|
| A single line appears, when idle | 16 ms | 16 ms |
| Longest time the window could not paint | **30 ms** | 55 ms |
| Paints per second during the load | **62** | 13 |
| Find a token that is not there, afterwards | **0.5 ms** | 12 ms |
| Page up, afterwards | **2.6 ms** | 7.9 ms |
| Total allocation | **91 MB** | 764 MB |
| Private memory over baseline | 29 MB | **18 MB** |

In both stacks the last line ends up on screen, and the idle time is at the resolution of the measurement. Under a burst, both skip lines that would be gone before anyone could see them.

### The library at its default size

The library keeps **50,000 lines** by default. The sink cannot be configured to do that, so these figures have no counterpart. For RichTextBox itself at that size, see the `control` suite.

| What the user experiences | Burst | Sustained |
|---------------------------|-------|-----------|
| A single line appears, when idle | 16 ms | 16 ms |
| Last line on screen, from the first message logged | 143 ms | 85 ms after the last message |
| Logging threads held up for | 115 ms | - |
| Longest time the window could not paint | 125 ms | 34 ms |
| Paints per second during the load | 27 | 54 |
| Find a token that is not there, afterwards | 12 ms | 12 ms |
| Page up, afterwards | 2.6 ms | 2.5 ms |
| Total allocation | 57 MB | 92 MB |
| Private memory over baseline | 66 MB | 70 MB |

The 125 ms in the burst is nearly the whole burst, because the timer behind this metric only runs once nothing else is waiting. Input is handled ahead of paint, and paint ahead of that timer, so this is an upper bound on what a user would feel.

### What the metrics mean

- **Longest time the window could not paint** is the longest gap between ticks of a UI timer. Windows delivers a timer only when no posted message, input or paint is waiting. A round trip with `Invoke` would not show this, because its posted message overtakes the input and paint that are starving.
- **Last line on screen** means the line is in the document and its position is inside the client area. The moment of presentation itself is not timed.

---

## Logger: no UI

One message with three parameters, on a single thread. `Discard` stops at the framework's output. `Format` also renders the message into a reused UTF-8 buffer. BenchmarkDotNet `ShortRun`, so differences of a few nanoseconds are not meaningful.

| Method | Mean | Allocated |
|--------|------|-----------|
| Serilog, discard | 98 ns | 488 B |
| Serilog, format | 155 ns | 488 B |
| ZLogger `LogInformation`, discard | 142 ns | 256 B |
| ZLogger `LogInformation`, format | 153 ns | 256 B |
| ZLogger `ZLogInformation`, discard | 131 ns | 0 B |
| ZLogger `ZLogInformation`, format | 156 ns | 0 B |
| Processor deferring to the pump, `LogInformation` | **191 ns** | **256 B** |
| Processor deferring to the pump, `ZLogInformation` | **149 ns** | **0 B** |
| Processor formatting on the logging thread, `LogInformation` | 298 ns | 457 B |
| Processor formatting on the logging thread, `ZLogInformation` | 275 ns | 201 B |

ZLogger and Serilog cost about the same per call to format a message. ZLogger allocates less.

A message holding only immutable values is deferred to the pump, so the logging thread pays about what ZLogger costs when it discards the message. These two rows are the logging thread's cost and not the whole cost: the formatting happens later on the pump, and they leave out the channel write. The `stack` suite measures all of it.

Any other message is formatted on the logging thread, because a value may be mutable or tied to that thread. The processor then adds about 120-145 ns and 201 B. All of these use a processor configured like a viewer with no linkifier. A linkifier needs every parameter's text, so it costs more.

---

## Control: no logging framework

The same 60,000 pre-built lines in batches of 1,000, with one color per line and word wrap on. Each control uses its fast common approach: `AppendText` and `SetStyling` for Scintilla, batched `SelectedRtf` with redraw suspended for RichTextBox. Undo is disabled on both. Median of 3 runs.

| Metric | Scintilla | RichTextBox |
|--------|-----------|-------------|
| Fill to 50k lines | **550 ms** | 2,516 ms |
| Allocation during fill | **46 MB** | 1,441 MB |
| Private memory over baseline | **43 MB** | 426 MB |
| Batch at the cap, append and trim | 289 ms | **84 ms** |
| Repaint | 1.55 ms | 1.61 ms |
| Page up | 2.2 ms | 2.8 ms |
| Find token near the end | **11 ms** | 275 ms |
| Find absent token | **11 ms** | 252 ms |
| Find first hit | **0.0013 ms** | 0.21 ms native, 23 ms via `RichTextBox.Find` |

- Repaint and page up are effectively tied.
- Scintilla is slower in the at-cap case. Scintilla remembers the lines waiting for word wrap as one range, so a trim at the start plus an append at the end queues the whole document to be wrapped again. The viewer avoids this by trimming in steps under load, which took its longest paint gap at the cap from about 105 ms to about 34 ms. This suite trims on every batch, as a plain use of the control would.
- `RichTextBox.Find` allocates about 83 MB on every search that finds a match. That cost belongs to the WinForms wrapper: calling `EM_FINDTEXTEXW` directly allocates about 0.1 KB. Native search time for a token near the end is about the same as the wrapper's.

---

## Limits

- One machine, 3 runs, no statistical tests.
- Hybrid cores are not pinned. One `logger` run measured the format rows about 1.5x slower partway through, while its discard rows did not change, so read small differences with care.
- WinForms on .NET 8 only. `net48`, `net10.0-windows` and WPF are not measured.
- Windows are visible but unfocused.
- A single idle line is timed by polling, and both stacks reach its resolution of about 16 ms.
- Messages are single-line text. JSON pretty-printing, exceptions, colorizers and linkifiers are not measured. Filter reapply is timed only by `FilterReplay_KeepsTheUiResponsive`, which uses a coarser probe.
