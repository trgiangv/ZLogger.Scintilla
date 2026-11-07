# Known Issues

Open limits of the current code. What was corrected is in the [changelog](../CHANGELOG.md).

## Limits

| Limit | Detail |
|-------|--------|
| The document can briefly exceed `MaxLines` | While logs keep arriving, it can hold up to a tenth more lines, because trimming on every drain queues the whole document for word wrap again. It returns to exactly `MaxLines` as soon as the logs pause |
| `RenderedMessages` can be lower than the messages logged | Lines that would be trimmed before anyone could see them are skipped. A filter replay renders its entries again and counts them again |
| LookupEngine's 33 public types are public in this assembly | An application that also references LookupEngine gets conflicting types. They are compiled in as source, so making them internal means changing the submodule |
| `ToString` can run twice per log call | Once for the message and once for its parameter span, when a linkifier is registered or the value gets its own color. A `ToString` with side effects, like logging, repeats them |
| A value with a format specifier is not colored | `{v:F2}` prints correctly, but its text no longer equals `value.ToString()`, so it is not found as a number |
| Create the viewer on the UI thread | Scintilla creates its window handle during construction, and that handle belongs to the constructing thread |
| `ILogColorizer` and `ILinkifier` must not wait for the UI thread | They run on the pump while it holds its lock, so `Clear()` called on the UI thread could deadlock |
| `CountMatchesAsync` can describe a moving document | It counts in slices while logs may still arrive, so its result is the document as the last slice saw it |
| `net48` has only a smoke test | Register, log a line, render it. The live tests and benchmarks run on `net8.0-windows` |
| Package metadata says MIT, and `LICENSE` holds Apache 2.0 text | Left as it is, by the owner's decision |

## Measured and not adopted

Options for the Scintilla control, measured on a bare control.

| Option | Result |
|--------|--------|
| `SCI_SETMODEVENTMASK` limited to insert and delete | No gain beyond noise |
| `SCI_SETMODEVENTMASK(0)` | No clear gain, and it breaks the public API: `Lines.Count` stays 1 |
| `SC_TECHNOLOGY_DIRECTWRITE` | Repaint and fill about 3x slower |
| `SCI_SETBUFFEREDDRAW(false)` | Repaint about 2.5x slower |
| `SCI_SETLAYOUTTHREADS(8)` | No change at these line lengths |
| `SCI_ALLOCATE(16 MB)` ahead of time | Fill about 8% faster, for 32 MB more private memory in an empty viewer |

ScintillaNET tracks UTF-16 positions per line for its public API, which costs a few native calls per line on the UI thread. Avoiding that would break `ScintillaControl.Lines` for hosts.
