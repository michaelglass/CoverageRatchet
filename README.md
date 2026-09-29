# CoverageRatchet

<!-- sync:intro:start -->
Per-file code coverage enforcement that only goes up. CoverageRatchet reads Cobertura XML coverage reports and enforces a threshold for every source file. When your tests improve coverage on a file, the threshold ratchets to the new level so it shouldn't drift back down.

| Package | What it is |
|---------|------------|
| [CoverageRatchet](src/CoverageRatchet/) | The `coverageratchet` dotnet tool: `check` in CI, `ratchet` locally after improving tests |
| [CoverageRatchet.Core](src/CoverageRatchet.Core/) | The embeddable library behind it: Cobertura parsing, threshold checking, ratcheting and merging, with no CLI |
<!-- sync:intro:end -->

> **Status: early alpha, and substantially AI-written.** It runs the author's own
> F# OSS repos daily, but behavior and APIs shift between versions and rough edges
> are expected — your mileage may vary. Issues and PRs welcome.

<!-- sync:getting-started:start -->
## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later

### Install from NuGet

```bash
# The CLI tool
dotnet tool install -g CoverageRatchet

# Or the library, to embed coverage ratcheting in your own tooling
dotnet add package CoverageRatchet.Core
```

### Quick verification

```bash
coverageratchet --help
```
<!-- sync:getting-started:end -->

<!-- sync:overview:start -->
## Overview

```bash
# Ratchet thresholds upward (default command)
coverageratchet

# Check current coverage against thresholds (use in CI)
coverageratchet check

# Lower failing files' floors to current coverage (makes check pass immediately)
coverageratchet loosen

# Record each file's current covered-LINE COUNT as a floor
coverageratchet baseline-lines

# Find files with lowest coverage
coverageratchet targets

# Show uncovered branch points per file
coverageratchet gaps
```

The default threshold for every file is **100% line and branch coverage**. Files that can't easily reach 100% (like CLI entry points) get per-file overrides with a documented reason.

Configuration lives in a `coverage-ratchet.json` file with two independent kinds of floor: **percentages** in `overrides` (`"line": 93` is 93 percent) and **absolute covered-line counts** in `countFloors` (`"coveredLines": 93` is 93 lines). Counts exist for the case where the percentage denominator is not trustworthy — the .NET collector only emits a source line once its method JIT-compiles, so the denominator drifts between runs while the numerator does not. See the [CoverageRatchet README](src/CoverageRatchet/) for the full configuration format, and the [CoverageRatchet.Core README](src/CoverageRatchet.Core/) for the library API.
<!-- sync:overview:end -->

## Development

### Building from source

```bash
git clone https://github.com/michaelglass/CoverageRatchet.git
cd CoverageRatchet
dotnet build CoverageRatchet.slnx
dotnet test --solution CoverageRatchet.slnx
```

### Project structure

```
src/
  CoverageRatchet/             # the coverageratchet dotnet tool
  CoverageRatchet.Core/        # the library it is built on
  Shared/                      # source files linked into CoverageRatchet
tests/
  CoverageRatchet.Tests/
  CoverageRatchet.Core.Tests/
  Tests.Common/                # shared test helpers
docs/                          # fsdocs content (synced from the READMEs)
```

### Running checks locally

With [mise](https://mise.jdx.dev/) installed:

```bash
mise run build       # Build all projects
mise run test        # Run all tests
mise run check       # Format, lint, docs and coverage, with auto-fix
mise run ci          # The same steps CI runs, with no auto-fix
```

This repo checks its own coverage with the CoverageRatchet it builds, not a
published version: every coverage task runs `dotnet run --project
src/CoverageRatchet/CoverageRatchet.fsproj`.

The local gate must run on **.NET SDK 10.0.4xx**. `global.json` pins 10.0.400 with
`rollForward: latestPatch`, and CI's build job uses the same band. Branch coverage
counts depend on the SDK feature band: the F# compiler in 10.0.3xx and in 10.0.4xx
emits different branch points for identical code, so a floor measured on one band
fails, or passes by accident, on the other. Run `mise run ci` from a plain shell,
where mise supplies the SDK. When moving to a new band, bump `global.json`, the
mise `dotnet` pin and CI's `dotnet-version` together and re-measure the branch
floors.

## License

MIT
