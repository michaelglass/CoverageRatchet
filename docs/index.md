# CoverageRatchet

<!-- sync:intro -->
Per-file code coverage enforcement that only goes up. CoverageRatchet reads Cobertura XML coverage reports and enforces a threshold for every source file. When your tests improve coverage on a file, the threshold ratchets to the new level so it shouldn't drift back down.

| Package | What it is |
|---------|------------|
| [CoverageRatchet](src/CoverageRatchet/) | The `coverageratchet` dotnet tool: `check` in CI, `ratchet` locally after improving tests |
| [CoverageRatchet.Core](src/CoverageRatchet.Core/) | The embeddable library behind it: Cobertura parsing, threshold checking, ratcheting and merging, with no CLI |
<!-- sync:intro:end -->

<!-- sync:getting-started -->
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

<!-- sync:overview -->
## Overview

```bash
# Ratchet thresholds upward (default command)
coverageratchet

# Check current coverage against thresholds (use in CI)
coverageratchet check

# Set thresholds to current coverage (makes check pass immediately)
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

See [CoverageRatchet](CoverageRatchet/) for the tool and [CoverageRatchet.Core](CoverageRatchet.Core/) for the library.
