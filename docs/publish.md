# Publishing to NuGet

ZLogger.Scintilla is published automatically via GitHub Actions when a version tag is pushed. This document describes the full release workflow.

---

## Prerequisites

- Write access to the repository
- `NUGET_API_KEY` secret configured in GitHub repository settings (`Settings → Secrets and variables → Actions`)
- All changes committed and pushed to `main`
- Tests passing on `main`

---

## Release Workflow

```mermaid
flowchart LR
    A["Bump version in .csproj"] --> B["Commit changes"]
    B --> C["git tag v0.x.y"]
    C --> D["git push origin v0.x.y"]
    D --> E["GitHub Actions triggered"]
    E --> F["dotnet restore + build -c Release"]
    F --> G["ILRepack merges LookupEngine"]
    G --> H["dotnet pack → .nupkg"]
    H --> I["dotnet nuget push → nuget.org"]
```

---

## Step-by-Step

### 1. Bump the version

Edit `ZLogger.Scintilla/ZLogger.Scintilla.csproj`:

```xml
<Version>0.0.4</Version>
```

### 2. Update CHANGELOG.md

Add a new section at the top of `CHANGELOG.md` following the existing format:

```markdown
## [0.0.4] — 2026-MM-DD

### Added
- ...

### Changed
- ...

### Fixed
- ...
```

### 3. Commit

```bash
git add ZLogger.Scintilla/ZLogger.Scintilla.csproj CHANGELOG.md
git commit -m "chore: release v0.0.4"
```

### 4. Create and push the tag

```bash
git tag v0.0.4
git push origin main
git push origin v0.0.4
```

> The tag push triggers `.github/workflows/publish.yml`. The workflow runs on `windows-latest` and uses `InProcessEmitToolchain` which requires STA thread (Windows-only).

### 5. Verify the publish

- Check the [Actions tab](../../actions) for the workflow run
- Confirm the package appears on [nuget.org/packages/ZLogger.Scintilla](https://www.nuget.org/packages/ZLogger.Scintilla)

---

## GitHub Actions Workflow

The workflow file is at `.github/workflows/publish.yml`:

```yaml
on:
  push:
    tags:
      - 'v*'
```

Steps executed:
1. `actions/checkout@v4` with `submodules: recursive` — required for LookupEngine submodule
2. `dotnet restore` on the main project
3. `dotnet build -c Release` — triggers ILRepack to merge `LookupEngine.dll` into `ZLogger.Scintilla.dll`
4. `dotnet pack -c Release --no-build` — packages the merged assembly
5. `dotnet nuget push` with `--skip-duplicate` — idempotent; safe to re-run if the tag already exists on NuGet

---

## Version Conventions

| Version | Meaning |
|---------|---------|
| `0.0.x` | Patch — bug fixes, docs, minor improvements |
| `0.x.0` | Minor — new features, non-breaking API additions |
| `x.0.0` | Major — breaking API changes |

---

## Troubleshooting

**Workflow not triggered:**
- Verify the tag was pushed to the remote: `git ls-remote --tags origin`
- Tags must match the `v*` pattern (e.g. `v0.0.4`, not `0.0.4`)

**ILRepack fails:**
- Ensure the `external/LookupEngine` submodule is initialized: `git submodule update --init --recursive`
- Build locally with `dotnet build ZLogger.Scintilla/ZLogger.Scintilla.csproj -c Release` to reproduce

**NuGet push rejected (version already exists):**
- `--skip-duplicate` suppresses this error; the workflow will still succeed
- To publish a fix, bump to the next patch version

**`NUGET_API_KEY` expired or missing:**
- Generate a new API key at [nuget.org/account/apikeys](https://www.nuget.org/account/apikeys)
- Update the secret in `Settings → Secrets and variables → Actions → NUGET_API_KEY`
