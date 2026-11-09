# Changelog

All notable changes to ZLogger.Scintilla are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions follow [Semantic Versioning](https://semver.org/).

## [0.1.1] - 2026-11-09

### Changed
- Locks are plain objects on every target again, instead of `System.Threading.Lock` on .NET 9 and later

### Fixed
- `IndexOutOfRangeException` from ScintillaNET's `LineCollection.TrackInsertText` after a host such as Revit destroyed the window holding the viewer and later gave it one again. ScintillaNET keeps the editor under a message-only window meanwhile, where its line index misses every edit. Logs now wait while the editor is there, and appear once it is back

## [0.1.0] - 2026-11-08

Whole-library correction. Rendering, the UI drain, scrolling, search and clicks now have tests on real windows with a running message loop, run on both `net48` and `net8.0-windows`. Benchmark figures in the entries below this one came from a suite that rendered nothing, and are withdrawn. See [docs/benchmarks.md](docs/benchmarks.md).

### Added
- `ScintillaOptions.WithScopes()` makes `BeginScope` values available to linkifiers. Off by default, because it costs every log call
- A find bar in the viewer's top-right corner, opened with Ctrl+F while the log has the focus, so the host application's shortcuts keep working elsewhere. Works in WinForms, WPF and hosts with their own message loop such as Revit, and fits panes down to 550px wide
- Find bar options: match case (Alt+C), whole word (Alt+W) and regular expression (Alt+R)
- Find in selection (Alt+L): limits search, highlighting and the count to a range the user selected
- Find bar keys: Enter and Shift+Enter, F3 and Shift+F3 for next and previous match, Escape to close
- A live "n of total" count that follows logs as they arrive and are trimmed, and a distinct mark on the current match
- `ScintillaNativeRuntimes` MSBuild property choosing which Scintilla native DLLs are deployed. By default win-x64 and win-arm64 only, instead of all three architectures; a project built for x86 gets win-x86 alone

### Changed
- URLs in log text are clickable with or without a linkifier, as this project always described. Only `http` and `https` qualify, because a click shell-executes the link
- Auto-scroll pauses while the user reads older lines or a search match, and resumes at the end of the document
- `Stop()` called on the UI thread returns without waiting for the pump, which stops after its current step. `Start()` waits for it before starting another. Called off the UI thread, `Stop()` still waits
- Colorizers and linkifiers run outside the pump's lock, and may wait for the UI thread
- A line logged after a quiet moment appears at once. Only lines close together wait for the flush interval
- `SetFilter` with history disabled applies to new lines only and keeps the document, because there is nothing to rebuild it from
- Regex search uses the ECMAScript grammar everywhere, and `CountMatchesAsync` returns 0 instead of throwing on an invalid pattern
- `MaxLines` keeps exactly that many messages once logs pause, and can be a tenth over while they arrive
- Lines that would be trimmed before anyone could see them are skipped, so `RenderedMessages` can be lower than the messages logged
- `Clear()` called off the UI thread clears the document in queue order and returns before it happens
- `SetPrettyJson` reapplies to existing lines at every filter level
- Messages holding only immutable values are formatted on the pump. Any other message, and everything past 4,096 pending entries, is formatted on the logging thread
- LookupEngine compiles into the assembly as source, which leaves one copy of Polyfill and no JetBrains.Annotations types
- `System.Threading.Channels` and `System.Text.Json` are package dependencies only on `net48`
- `CommunityToolkit.HighPerformance` is no longer a dependency
- `LogEntry` is internal, since nothing public accepted or returned one
- `ScintillaOptions.EnableTokenLinks` is removed, since links no longer depend on a linkifier
- Locks use `System.Threading.Lock` on .NET 9 and later
- `FindNext` and `FindPrevious` move the caret to the match and mark it instead of selecting it, and continue from wherever the user left the caret. A selection covered the match's mark, and replaced the user's own selection

### Fixed
- Auto-scroll with word wrap stopping short of the last message
- UI thread hang on `SetFilter` with an active search
- Rendering stopping for good after an exception, with history disabled and a filter, or under filter replay
- Lines lost during filter replay, and released buffers reaching the document during history trim
- `DroppedMessages` staying 0 and `PendingEntryCount` drifting with a drop policy
- A backlog behind a blocked UI thread growing without limit
- Filter replay holding the UI thread for its whole length
- Auto-scroll moving the caret, and `FindNext` not scrolling to its match
- Link tokens and search highlights drifting with non-ASCII text
- Search rank off by one, and regex navigation disagreeing with the match count
- A cancelled match count bringing back the pattern a newer search had replaced
- Timestamps printed in UTC instead of local time
- Format specifiers, alignment and `{{ }}` escapes being lost
- A throwing `ToString` losing the entry, or reaching the application's logging call
- A `ToString` or property that logs on the same thread corrupting the message being formatted
- Custom serializers producing an empty parameter span, and JSON escaping non-ASCII text and `&`
- A link opening three times on a double click, and opening at the end of a drag that selects text
- A URL split in two by a parameter value inside it
- URLs styled as links with no way to click them, and left unstyled in messages with no parameters
- Local files and other programs reachable by clicking a link in log text
- The first `Clear()` releasing the original document twice
- Pooled arrays keeping click actions alive
- `net48` NuGet consumers failing at runtime: missing native DLLs, then a `System.Text.Json` version conflict
- Deadlock when a colorizer or linkifier waited for the UI thread while the UI thread called `Clear()`, `Stop()` or `Dispose()`, as closing a window does
- XML documentation stating a default collection limit of 50, where the default is 20
- XML documentation stating that a colorizer still colors a token a linkifier matched

---

## [0.0.9] - 2026-08-16

### Changed
- Scintilla5.NET 7.0.0 (native Scintilla 5.6.1 / Lexilla 5.4.8)
- Added `net10.0-windows` target framework
- LookupEngine submodule updated to latest main (includes member revaluation 2.0.6+)
- Polyfill is referenced only on net48; Directory.Build.targets strips it on modern TFMs so ILRepack/CoreCLR 10 does not smash `Polyfills.Polyfill`
- ILRepack merges copy-local project outputs via `IsRepackable` (no hardcoded assembly names)

---

## [0.0.8] - 2026-07-04

### Added
- `ICustomSerializer` — to override default serialization behavior

### Changed
- #**Max collection items** and **Max decompose depth** configurable at runtime in `ScintillaOptions`

---

## [0.0.7] - 2026-07-03

### Fixed
- AccessViolationException native memory corruption

### Changed
- Update package references to latest versions

---

## [0.0.6] - 2026-03-22

### Added
- JSON decomposition depth mutable at runtime

### Fix
- JsonSerializer.Serialize conflict with deep nested

## [0.0.5] - 2026-03-22

### Fix
- Reduce maximun json decomposition depth


## [0.0.4] — 2026-03-21

### Added
- `JsonPipelineBenchmark` — measures `EnablePrettyJson` overhead for plain and structured messages
- `EnumerableSerializationTests` (8 tests) — verifies `IEnumerable` parameters are not double-serialized by ZLogger's default formatter
- `BatchPreparerTests` (10 tests) — validates batch preparation logic including JSON handling
- `SolutionStructureTests` — file/folder organization validation
- `TestHelper` utility class with `MakeEntry(text, level)` and `MakeEntry(text, level, properties)` factory methods
- Demo: `IEnumerable` parameter showcase (List, array, custom objects, empty collection)
- Demo: `PrettyJson` and `Line Numbers` checkboxes in toolbar
- Selection highlight colors: dark mode (`Color.FromArgb(60, 80, 120)`) and light mode (`Color.FromArgb(173, 214, 255)`) with matching caret colors
- `docs/publish.md` — complete NuGet release workflow guide
- `CHANGELOG.md` — this file

### Changed

#### Pipeline
- `ScintillaLogProcessor.Post()` — added early return with `log.Return()` when disposed, preventing post-dispose processing
- `ScintillaLogProcessor.WriteDecomposedValue()` — inlined `TryWriteRecursiveDecompose`, replaced `WritePrimitiveValue(string)` with `WriteJsonValue(object)` that writes typed values directly (`bool`, `int`, `long`, `double`, etc.) instead of parsing strings back to typed JSON
- `ScintillaLogProcessor.WriteEnumerable()` — uses `WriteJsonValue(item)` instead of `WritePrimitiveValue(item.ToString())`
- `ScintillaLogProcessor` — removed unnecessary `[MethodImpl(AggressiveInlining)]` from `GetThreadTextBuffer`, `GetThreadJsonBuffer`, `RentArray`, `ReleaseArray`; removed `ZLoggerOptions` dependency — formatter created internally, error handling simplified
- `LoggingBuilderExtensions` — removed `Action<ZLoggerOptions>? configureZLogger` parameter from `AddZLoggerScintillaWinForms` and `AddZLoggerScintillaWpf` (**breaking change**); `ScintillaLogFormatter` is now the only formatter, applied unconditionally

#### Rendering
- `ScintillaLogFormatter` — timestamp format changed from `HH:mm:ss.fff` (12 bytes) to `HH:mm:ss` (8 bytes); removed `WriteThreeDigits`, `WriteStringUtf8` (inlined as `WriteSpanUtf8`); removed `[MethodImpl(AggressiveInlining)]` from `ResolveParameterText`, `GetLevelAbbreviation`
- `LogPrefixParser` — removed unused `TryParse(string, out string)` and `BuildPrefixSegments(ParsedPrefix, string, ...)` string overloads; removed `GetUtf8ByteCount` helper; removed `[MethodImpl(AggressiveInlining)]` from `ParseShortLevel`
- `JsonSyntaxHighlighter` — updated doc comment to reference `ScintillaLogProcessor` via LookupEngine instead of stale `SystemTextJsonZLoggerFormatter`
- `UrlScanner` — changed from `sealed class` with instance `_matchBuffer` field to `static class`; removed `AppendUrlAwareContent` instance method (callers use static `FindAll` + `EmitUrlOrFallback` directly)
- `RenderingChars.IsTokenSeparator()` — simplified from explicit char matching to `TokenSeparators.IndexOf(ch) >= 0`
- `LogRenderer` constructor — changed from `public` to `internal`; added `ApplySelectionStyles()` for theme-aware selection/caret colors

#### Models
- `ParameterSpan.LeafTypes` — trimmed from 17 types to 7 essential types (`string`, `decimal`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Guid`, `byte[]`, `ReadOnlyMemory<byte>`, `ArraySegment<byte>`); primitive numeric types (`int`, `long`, `double`, `bool`, `char`, etc.) removed since they are handled directly by `WriteJsonValue`
- `TokenPayload` — removed `SemanticStyle` property and constructor parameter; `ForLink()` and `ForColorizer()` simplified

#### Services
- `ScintillaDocument.Clear()` — replaced `BeginMutableSection` / `ClearAll` / `EndMutableSection` with document swap (`CreateDocument` + `ReleaseDocument`); added `_tokenRanges.TrimExcess()`; re-applies `WrapMode`, `ReadOnly`, undo collection after swap
- `NativeWindowsHelper` — removed `DwmSetWindowAttribute` (unused for scrollbar theming); renamed `ApplyNativeTheme` → `ApplyScrollbarTheme`; added `ApplyContextMenuTheme` using `uxtheme.dll` ordinals `#135` (`SetPreferredAppMode`) and `#136` (`FlushMenuThemes`) for native dark context menu
- `StyleMapper` — removed `[MethodImpl(AggressiveInlining)]` from `ToStyleId` methods
- `ScintillaLogViewerBase` — calls both `ApplyScrollbarTheme` and `ApplyContextMenuTheme` when theme changes

#### Public API
- `ILinkifier` — simplified doc comment (removed stale "multiple linkifiers" reference)
- `ScintillaLogViewer.DisposeHostResources()` — changed to no-op (Scintilla control disposal handled by parent)

#### Demo
- Multi-target: `net48;net8.0-windows` (was `net8.0-windows` only)
- `Program.cs` — `#if NET8_0_OR_GREATER` guard for `ApplicationConfiguration.Initialize()` vs `EnableVisualStyles()`
- `MainForm` — removed `PlaceholderText` (not available on net48); `Environment.TickCount64` → `Environment.TickCount` with `unchecked` for net48 compat
- `DemoRevitColorizer` — replaced `"aurecon"u8` literal and `ReadOnlySpan<byte>.Trim()` with `byte[]` array and manual trim loop for net48 compatibility
- `DemoRevitLinkifier` — `Encoding.UTF8.GetString(ReadOnlySpan<byte>)` → `GetString(byte[])` via `.ToArray()` for net48

#### Tests
- `AllocationBudgetTests`, `ConcurrencyTests`, `HostLifecycleTests`, `LogHistoryBufferTests` — refactored to use `TestHelper.MakeEntry()`
- `ParameterSpanTests` — simplified to match reduced `LeafTypes` set
- `DocumentationConsistencyTests` — updated stale type references
- `LogPrefixParserTests` — added `StillParsesMillisecondFormat` test; updated prefix byte length expectations for `HH:mm:ss` format
- `UrlClickTests` — added `EmitUrlOrFallback` with `preserveStyle`, `HasPotentialCandidate` theory, `FindAll` count, `TryNormalizeUri` tests

#### Docs
- `docs/benchmarks.md` — updated all numbers from latest benchmark run (withdrawn since: that suite rendered nothing)
- `docs/testing.md` — added `EnumerableSerializationTests`, updated test count (~136), added per-file test counts, new test contract sections
- `docs/architecture.md` — added "Clear / Memory Release" section with document swap diagram; updated timestamp format references

### Fixed
- Memory not fully released after `Clear()` — native Scintilla document buffer and managed `LineCollection.GapBuffer<PerLine>` retained capacity after `SCI_CLEARALL`; fixed via document swap
- `_tokenRanges` backing array retained large capacity after clear — fixed with `TrimExcess()`
- `IEnumerable` parameters double-serialized — ZLogger's default formatter auto-serialized non-`IFormattable` `IEnumerable` types; fixed by removing `configureZLogger` entirely and applying `ScintillaLogFormatter` unconditionally
- `ScintillaLogProcessor` continued processing after dispose — added early return guard in `Post()`
- Context menu remained white in dark theme — added native Win32 `SetPreferredAppMode` / `FlushMenuThemes`
- Selection highlight color not appropriate for dark mode — added theme-aware selection background and caret colors

---

## [0.0.3] — 2026-03-21

### Added
- `JsonPipelineBenchmark` class (committed in this tag but benchmarks expanded post-tag)
- Clickable JSON links in pretty-print output

### Changed
- Benchmark and test documentation updated

---

## [0.0.2] — 2026-03-21

### Added
- `ILRepack` integration — `LookupEngine.dll` + `LookupEngine.Abstractions.dll` merged into `ZLogger.Scintilla.dll`; NuGet package ships a single assembly with no external LookupEngine dependency
- Line numbers margin (mutable, themed, togglable via `ShowLineNumbers`)
- `ScintillaOptions.EnablePrettyJson` — pretty-prints complex object parameters as indented JSON below the log line
- `LookupEngine` git submodule at `external/LookupEngine` for safe reflection-based object decomposition

### Changed
- `ScintillaLogViewerBase` promoted from interface to abstract base class; `ScintillaViewerControl` removed
- DI registration simplified: viewer created inside `AddZLoggerScintillaWinForms` / `AddZLoggerScintillaWpf`; external access via `IServiceProvider.GetService<ScintillaLogViewer>()`
- `IScintillaLogViewer` + `IScintillaLogViewHost` consolidated into `ScintillaLogViewerBase`
- `ScintillaOptions` absorbs `ScintillaViewerBuilder` configuration
- `FilterReapplyCoordinator` refactored — no god-class parameter bag
- `RenderBatch` renamed from `PendingUiBatch`; `LogConstants` merged from `LogPropertyKeys`
- `LogRenderContext` → `RenderContext`; `LogPrefixParseResult` → `ParsedPrefix`
- `JsonValueFormatter` → `JsonSyntaxHighlighter`
- `LogFilterEngine` renamed for clarity

### Fixed
- `System.InvalidOperationException: Collection was modified` concurrency bug in filter reapply
- `ILogColorizer` and `ILinkifier` not invoked correctly after pipeline refactor
- `WithLinkify` not working after options refactor
- JSON object cycle exception (`JsonSerializationException`) during pretty-print serialization

---

## [0.0.1] — Initial release

### Added
- Core pipeline: `ScintillaLogProcessor` → `LogIngestPump` → `BatchPreparer` → `ScintillaDocument`
- Structured log rendering with semantic syntax highlighting (level, timestamp, category, message, parameters)
- `IScintillaTheme` — built-in Dark and Light themes
- `ILogColorizer` — per-token color override hook
- `ILinkifier` — per-token clickable link hook
- `UrlScanner` — automatic URL/email linkification
- `LogHistoryBuffer` — circular buffer with chunked snapshot for filter reapply
- `SearchCoordinator` — async highlight + `FindNext` / `FindPrevious` with Boyer-Moore native search
- `FilterReapplyBenchmark`, `IngestThroughputBenchmark`, `LargeDocumentSearchBenchmark`, `PaintBenchmark`, `SustainedLoadBenchmark`
- WinForms (`ScintillaLogViewer`) and WPF (`ScintillaLogViewerWpf`) hosts
- `AddZLoggerScintillaWinForms` / `AddZLoggerScintillaWpf` DI extension methods
