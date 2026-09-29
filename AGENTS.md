# AGENTS.md

Guidance for AI coding agents working in this repo. `CLAUDE.md` points here.

## Project

CoverageRatchet — an F# repo publishing two NuGet packages: the
`coverageratchet` dotnet tool and the `CoverageRatchet.Core` library it is built
on. It was split out of
[MichaelsWackyFsPackageTools](https://github.com/michaelglass/MichaelsWackyFsPackageTools)
with its history and release tags, and still uses that repo's reusable CI,
release and docs workflows.

```
src/CoverageRatchet/              the dotnet tool (PackageId CoverageRatchet)
src/CoverageRatchet.Core/         the library (PackageId CoverageRatchet.Core)
src/Shared/                       GitDir.fs, GitStoreEnvironment.fs — linked into
                                  CoverageRatchet; copies of the same files in
                                  MichaelsWackyFsPackageTools
tests/CoverageRatchet.Tests/      tests for CoverageRatchet
tests/CoverageRatchet.Core.Tests/ tests for CoverageRatchet.Core
tests/Tests.Common/               shared test helpers (not a test project)
docs/                             fsdocs content, generated from the READMEs
```

**One test project per package.** That is not stylistic: CI derives a coverage
directory from each test project's folder name minus `.Tests`, then requires a
matching `coverage-ratchet-<name>.json` at the root for every such config.

## Before you claim done

Run `mise run ci`. It runs, in CI's order: format check, docs sync check, build
with `--warnaserror`, FSharpLint, fsdocs, tests with coverage, the coverage
ratchet, and fsprojlint. Keep it in step with `.github/workflows/ci.yml` — a
local `ci` that runs a different set from real CI can go green on work CI will
reject.

`mise run check` is the same ground with auto-fix (it formats and syncs docs
rather than failing on them).

Don't skip format. Fantomas is strict; a wrapped line in the wrong place fails
CI.

## Dogfooding

This repo checks its own coverage with the CoverageRatchet it builds. Every
coverage task in `mise.toml` and the CI `coverageratchet-cmd` input run
`dotnet run --project src/CoverageRatchet/CoverageRatchet.fsproj`; the published
`coverageratchet` tool is deliberately **not** in `.config/dotnet-tools.json`.

## Task runner

`mise.toml`. The SDK is pinned there (`dotnet = "10.0.400"`), in `global.json`
(10.0.400, `latestPatch`) and in CI (`dotnet-version: '10.0.4xx'`). Branch counts
depend on the SDK feature band, so move all three together and re-measure the
floors. Run `mise run ci` from a plain shell; a shell exporting `DOTNET_ROOT` for
another SDK overrides mise, and the pin refuses it by design.

```
mise run build                    mise run test               mise run test-coverage
mise run format                   mise run format-check       mise run lint
mise run lint-project             mise run sync-docs          mise run sync-docs-check
mise run coverage-check           mise run coverage-ratchet   mise run coverage-loosen
mise run coverage-baseline-lines  mise run loosen-from-ci     mise run docs
mise run pack                     mise run check / ci         mise run changelog-check
mise run release                  mise run release-dry-run    mise run release-alpha
```

## CI

`.github/workflows/ci.yml` calls two reusable workflows from
MichaelsWackyFsPackageTools — `michaels-wacky-build.yml` (Linux build, tests,
coverage; plus a Windows build-and-test job) and
`michaels-wacky-lint-project.yml`.

- **Inputs are validated.** GitHub rejects a reusable-workflow call that passes
  an input the called workflow does not declare, and the run fails before
  anything compiles. Read the called workflow's `on.workflow_call.inputs` before
  adding one.
- **fsprojlint is a JOB, not just a mise task.** A gate that lives only in the
  task runner is not on the path CI executes. If you add a check, add it to both.

## Coverage

CoverageRatchet enforces per-file, per-platform floors from
`coverage-ratchet-CoverageRatchet.json` and
`coverage-ratchet-CoverageRatchet.Core.json`. **A file with no entry defaults to
100% line and 100% branch**, not to a weaker fallback.

Each test project's report measures its own package only:
`tests/CoverageRatchet.Tests/testconfig.json` limits that project's coverage to
`CoverageRatchet.dll`, so Core's files are floored once, by `CoverageRatchet.Core.Tests`.

- `mise run coverage-check` — run tests and check the floors.
- `mise run coverage-ratchet` — **tighten** existing floors after coverage
  improves. It never adds a new platform entry.
- `mise run coverage-loosen` — **add** a missing entry for the current platform
  from actual coverage. Always write a `reason`.
- `mise run loosen-from-ci` — integrate the CI platform's floors from a CI run's
  `coverage-thresholds` artifact.

Floors are platform-specific. A Linux floor comes only from a green Linux CI run;
never loosen a floor from a macOS measurement to paper over a Linux failure, and
never re-baseline from a measurement taken on a different SDK band. Don't
hand-edit the JSON.

## Tests

- xUnit v3 + Microsoft Testing Platform, Unquote for assertions (`test <@ ... @>`).
- Reuse helpers from `tests/Tests.Common/TestHelpers.fs` — `withTempDir`,
  `withCapturedConsole`, `createTempDir`, `cleanupDir`.
- Prefer real code over mocks. File I/O in a temp dir is fine and common.
- Run a single project:
  `dotnet test --project tests/CoverageRatchet.Tests/CoverageRatchet.Tests.fsproj`.

## Docs

`syncdocs` copies marked sections out of `README.md` into `docs/index.md`, and
`src/<Project>/README.md` into `docs/<Project>/index.md`. The README is the
authoritative copy. Run `mise run sync-docs` after editing a README; CI fails on
drift.

## Packing and releasing

- `mise run pack` produces a **ref-stamped** package: RefStamp (wired in the root
  `Directory.Build.props`) suffixes a local pack's version with the jj/git source
  ref, so a dev machine cannot produce a release-shaped version.
- `mise run release` runs `ci` and then `fssemantictagger release`, which derives
  each changed package's bump level from its public API diff, bumps `<Version>`,
  promotes that package's `## Unreleased` CHANGELOG section, commits, tags and
  pushes. Each package has its own `CHANGELOG.md` and needs a non-empty
  `## Unreleased` section — `mise run changelog-check` tells you.
- Tag prefixes are per package (`coverageratchet-v`, `coverageratchet-core-v`),
  configured in `semantic-tagger.json`. The existing tags were carried over from
  MichaelsWackyFsPackageTools, so the next version continues from them.
- The tag triggers `.github/workflows/release.yml`: the shared workflow packs and
  creates the GitHub Release, then a per-package `publish` job **in this repo**
  exchanges an OIDC token for a NuGet key (`nuget/login`) and pushes. That job
  must live here: NuGet Trusted Publishing checks the token's `job_workflow_ref`
  against the calling repo's own workflow file. It needs the `NUGET_USER`
  repository variable and this repo registered as a trusted publisher for both
  packages on nuget.org.

## Version control

jj (Jujutsu), colocated with git. `jj describe -m "..."` — always with `-m`;
without it jj opens `$EDITOR` and waits. `jj new` starts a new change. A
**non-colocated** checkout (`jj git init`, no `.git` at the root) is also
supported: the root `Directory.Build.props` disables SourceLink and the SCM
queries when `.git` is absent, which is what lets the repo pack itself locally.
