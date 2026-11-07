# Known Issues

Open limits of the current code. What was corrected is in the [changelog](../CHANGELOG.md).

## Limits

| Limit | Detail |
|-------|--------|
| Conflicts with a separate LookupEngine reference | LookupEngine's public types are compiled into this assembly, so an application that also references LookupEngine gets duplicate types |
| `ToString` can run twice per log call | When a linkifier is registered or a value gets its own color. A `ToString` with side effects, like logging, repeats them |
| A value with a format specifier is not colored | `{v:F2}` prints correctly, but is shown as plain text instead of as a number |
| The document can briefly exceed `MaxLines` | By up to a tenth while logs keep arriving. It returns to exactly `MaxLines` as soon as they pause |
