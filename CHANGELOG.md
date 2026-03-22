# Changelog

All notable changes to ZLogger.Scintilla are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions follow [Semantic Versioning](https://semver.org/).

---

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
- `docs/benchmarks.md` — updated all numbers from latest benchmark run; SustainedLoad full pipeline 78.5 ms → 43.3 ms (45% faster)
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
