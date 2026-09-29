module CoverageRatchet.Ratchet

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds

let private toThreshold (pct: float) = floor pct

let ratchet (config: Config) (files: FileCoverage list) : Config =
    let fileMap = files |> List.map (fun f -> f.FileName, f) |> Map.ofList

    let newOverrides =
        config.Overrides
        |> Map.toList
        |> List.choose (fun (name, ovr) ->
            match Map.tryFind name fileMap with
            | None -> Some(name, ovr)
            | Some file ->
                let newLine = max ovr.Line (toThreshold file.LinePct)
                let newBranch = max ovr.Branch (toThreshold file.BranchPct)

                if newLine >= config.DefaultLine && newBranch >= config.DefaultBranch then
                    None
                else
                    Some(
                        name,
                        { ovr with
                            Line = newLine
                            Branch = newBranch
                        }
                    ))
        |> Map.ofList

    { config with Overrides = newOverrides }

/// What a ratchet did. `ratchetWithStatus` carries the new `RawConfig`;
/// `ratchetRawWithStatus` carries a `RawChange`, which also says which floors moved.
type RatchetStatus<'change> =
    | NoChanges
    | Tightened of 'change
    | Failed of 'change * failedFiles: string list

/// One number an operation moved in a floor entry that was already in the config,
/// e.g. `Thresholds.fs`, `Some MacOS`, `"branch"`, 91 -> 90. `Field` is the key the
/// config file gives the number; `Reason` is the entry's, kept as written.
type FloorMove =
    {
        File: string
        Platform: Platform option
        Field: string
        Old: float
        New: float
        Reason: string option
    }

/// What a raw operation did: the config to save, each number it moved in an existing
/// entry, and the files whose floor for the running platform it added or removed.
type RawChange =
    {
        Config: RawConfig
        Moves: FloorMove list
        Added: string list
        Removed: string list
    }

let ratchetWithStatus (config: Config) (files: FileCoverage list) : RatchetStatus<RawConfig> =
    let failedFiles =
        buildFileResults config files
        |> List.filter (fun r -> not (FileResult.passed r))
        |> List.map (fun r -> r.File.FileName)
        |> List.append (
            buildCountResults config files
            |> List.filter (fun r -> not (CountResult.passed r))
            |> List.map (fun r -> r.File.FileName)
        )
        |> List.distinct

    let newConfig = ratchet config files
    let newRaw = toRawConfig newConfig

    if not (List.isEmpty failedFiles) then
        Failed(newRaw, failedFiles)
    elif newConfig.Overrides <> config.Overrides then
        Tightened newRaw
    else
        NoChanges

/// What the raw merge needs to know about one kind of floor. `Values` and `CopyValues`
/// are the only parts that know which numbers the kind carries.
[<NoEquality; NoComparison>]
type private FloorKind<'a> =
    {
        Platform: 'a -> Platform option
        WithPlatform: Platform option -> 'a -> 'a
        Reason: 'a -> string option
        /// Each number the kind carries, under the key the config file gives it.
        Values: 'a -> (string * float) list
        /// The second entry with the first one's numbers.
        CopyValues: 'a -> 'a -> 'a
    }

let private percentageFloor: FloorKind<Override> =
    {
        Platform = fun o -> o.Platform
        WithPlatform = fun p o -> { o with Platform = p }
        Reason = fun o -> o.Reason
        Values = fun o -> [ "line", o.Line; "branch", o.Branch ]
        CopyValues =
            fun src tgt ->
                { tgt with
                    Line = src.Line
                    Branch = src.Branch
                }
    }

let private countFloor: FloorKind<CountFloor> =
    {
        Platform = fun f -> f.Platform
        WithPlatform = fun p f -> { f with Platform = p }
        Reason = fun f -> f.Reason
        Values =
            fun f ->
                [
                    "coveredLines", float f.CoveredLines
                    "coveredBranches", float f.CoveredBranches
                ]
        CopyValues =
            fun src tgt ->
                { tgt with
                    CoveredLines = src.CoveredLines
                    CoveredBranches = src.CoveredBranches
                }
    }

/// Each number that differs between `old` and `updated`, two versions of one entry.
let private movesOf (kind: FloorKind<'a>) (name: string) (old: 'a) (updated: 'a) : FloorMove list =
    List.zip (kind.Values old) (kind.Values updated)
    |> List.choose (fun ((field, oldValue), (_, newValue)) ->
        if oldValue = newValue then
            None
        else
            Some
                {
                    File = name
                    Platform = kind.Platform old
                    Field = field
                    Old = oldValue
                    New = newValue
                    Reason = kind.Reason old
                })

/// Fold a resolved before/after pair back into the platform-structured raw map,
/// touching only the entry that resolved for the running platform and leaving
/// other platforms' entries untouched, and say what moved.
///
/// An entry present in `after` but not `before` is NEW. For a file with no
/// floor at all it is written platform-less: a platform-tagged floor is
/// invisible to every other platform's CI, so nothing may synthesise one
/// implicitly. For a file that already carries other platforms' floors — a
/// Linux count captured from CI, say — the new entry is tagged with the
/// platform this run measured, because a platform-less entry beside a Linux
/// one would claim every platform except the one that was measured.
let private mergeRawSection
    (kind: FloorKind<'a>)
    (rawEntries: Map<string, 'a list>)
    (before: Map<string, 'a>)
    (after: Map<string, 'a>)
    : Map<string, 'a list> * FloorMove list * added: string list * removed: string list =
    let mutable result = rawEntries
    let moves = ResizeArray<FloorMove>()
    let added = ResizeArray<string>()
    let removed = ResizeArray<string>()

    for kv in before do
        let name = kv.Key
        let existingEntries = Map.tryFind name rawEntries |> Option.defaultValue []

        let hasPlatformSpecific =
            existingEntries
            |> List.exists (fun e -> kind.Platform e = Some Platform.current)

        let isResolvingEntry entry =
            kind.Platform entry = Some Platform.current
            || (kind.Platform entry = None && not hasPlatformSpecific)

        match Map.tryFind name after with
        | Some updatedValue ->
            let updated =
                existingEntries
                |> List.map (fun entry ->
                    if isResolvingEntry entry then
                        let copied = kind.CopyValues updatedValue entry
                        moves.AddRange(movesOf kind name entry copied)
                        copied
                    else
                        entry)

            result <- Map.add name updated result
        | None ->
            removed.Add name

            let remaining =
                existingEntries |> List.filter (fun entry -> not (isResolvingEntry entry))

            if List.isEmpty remaining then
                result <- Map.remove name result
            else
                result <- Map.add name remaining result

    for kv in after do
        if not (Map.containsKey kv.Key before) then
            let existingEntries = Map.tryFind kv.Key rawEntries |> Option.defaultValue []

            let platform =
                if existingEntries |> List.exists (fun e -> (kind.Platform e).IsSome) then
                    Some Platform.current
                else
                    None

            added.Add kv.Key
            result <- Map.add kv.Key (existingEntries @ [ kind.WithPlatform platform kv.Value ]) result

    result, List.ofSeq moves, List.ofSeq added, List.ofSeq removed

let private mergeRawOverrides (raw: RawConfig) (resolvedBefore: Config) (resolvedAfter: Config) : RawChange =
    let entries, moves, added, removed =
        mergeRawSection percentageFloor raw.RawOverrides resolvedBefore.Overrides resolvedAfter.Overrides

    {
        Config = { raw with RawOverrides = entries }
        Moves = moves
        Added = added
        Removed = removed
    }

let private mergeRawCountFloors (raw: RawConfig) (resolvedBefore: Config) (resolvedAfter: Config) : RawChange =
    let entries, moves, added, removed =
        mergeRawSection countFloor raw.RawCountFloors resolvedBefore.CountFloors resolvedAfter.CountFloors

    {
        Config = { raw with RawCountFloors = entries }
        Moves = moves
        Added = added
        Removed = removed
    }

// ---------------------------------------------------------------------------
// Count floors
//
// Gating on the NUMERATOR. Covered-line count is stable for unchanged code
// because it does not divide by the JIT-dependent emitted-line set (ADR 0019).
//
// The cost, stated plainly: a count floor cannot tell a deleted TEST (a real
// regression) from deleted COVERED CODE (a legitimate refactor). Both lower the
// count. The only signal that would distinguish them is the total emitted-line
// count — precisely the quantity ADR 0019 proved unreliable — so guessing from
// it would reintroduce the non-determinism this design exists to avoid.
//
// Therefore a legitimate decrease is resolved by a HUMAN re-baseline, and the
// re-baseline is deliberately the same one-word command used to bootstrap
// (`baseline-lines`), so the routine path is the well-trodden path.
// ---------------------------------------------------------------------------

/// Raise count floors toward current counts. Monotonic — never lowers.
///
/// Only files that already HAVE a floor are touched: an ordinary ratchet run
/// must not silently enrol new files, or an impact-filtered partial run would
/// write floors from coverage that never ran.
let ratchetCountFloors (config: Config) (files: FileCoverage list) : Config =
    let fileMap = files |> List.map (fun f -> f.FileName, f) |> Map.ofList

    let newFloors =
        config.CountFloors
        |> Map.map (fun name floor ->
            match Map.tryFind name fileMap with
            | None -> floor
            | Some file ->
                { floor with
                    CoveredLines = max floor.CoveredLines file.LinesCovered
                    CoveredBranches = max floor.CoveredBranches file.BranchesCovered
                })

    { config with CountFloors = newFloors }

/// Record current counts as the floor for every observed file.
///
/// This is both the bootstrap and the re-baseline, and it CAN lower a floor —
/// that is the point. Run it after deliberately removing covered code. Existing
/// reasons are preserved so a recorded justification is not silently dropped.
let baselineCountFloors (config: Config) (files: FileCoverage list) : Config =
    let newFloors =
        files
        |> List.fold
            (fun acc (file: FileCoverage) ->
                let reason =
                    Map.tryFind file.FileName acc |> Option.bind (fun (f: CountFloor) -> f.Reason)

                Map.add
                    file.FileName
                    {
                        CoveredLines = file.LinesCovered
                        CoveredBranches = file.BranchesCovered
                        Reason = reason
                        Platform = None
                    }
                    acc)
            config.CountFloors

    { config with CountFloors = newFloors }

let ratchetCountFloorsRaw (raw: RawConfig) (files: FileCoverage list) : RawChange =
    let resolved = resolveConfig raw
    let ratcheted = ratchetCountFloors resolved files
    mergeRawCountFloors raw resolved ratcheted

/// A floor baselined for a file that had none is written PLATFORM-LESS, so one
/// baseline run guards every platform. The alternative — tagging it with the
/// machine that measured it — would make a floor baselined on macOS invisible
/// to a Linux-only CI, which is exactly how a red macOS percentage floor went
/// unseen by remote CI. A file that already carries another platform's floor
/// gets an entry for the measured platform instead; see `mergeRawSection`.
let baselineCountFloorsRaw (raw: RawConfig) (files: FileCoverage list) : RawChange =
    let resolved = resolveConfig raw
    let baselined = baselineCountFloors resolved files
    mergeRawCountFloors raw resolved baselined

let ratchetRaw (raw: RawConfig) (files: FileCoverage list) : RawChange =
    let resolved = resolveConfig raw
    let overrides = mergeRawOverrides raw resolved (ratchet resolved files)
    let counts = ratchetCountFloorsRaw overrides.Config files

    {
        Config = counts.Config
        Moves = overrides.Moves @ counts.Moves
        Added = overrides.Added @ counts.Added
        Removed = overrides.Removed @ counts.Removed
    }

/// Files failing EITHER the percentage floors or their count floor.
let private allFailedFiles (resolved: Config) (files: FileCoverage list) =
    let pctFailures =
        buildFileResults resolved files
        |> List.filter (fun r -> not (FileResult.passed r))
        |> List.map (fun r -> r.File.FileName)

    let countFailures =
        buildCountResults resolved files
        |> List.filter (fun r -> not (CountResult.passed r))
        |> List.map (fun r -> r.File.FileName)

    pctFailures @ countFailures |> List.distinct

let ratchetRawWithStatus (raw: RawConfig) (files: FileCoverage list) : RatchetStatus<RawChange> =
    let failedFiles = allFailedFiles (resolveConfig raw) files
    let change = ratchetRaw raw files

    if not (List.isEmpty failedFiles) then
        Failed(change, failedFiles)
    elif change.Config <> raw then
        Tightened change
    else
        NoChanges

/// Lower the floors of the files in `files` that fail them, to their current
/// coverage, and leave every other entry exactly as it is.
///
/// Only a failing number moves, and only down: a file failing its line floor keeps
/// its branch floor. A failing file without an override gets one, with
/// `reason = "loosened automatically"`. A passing file is never touched, so a floor
/// set on purpose (a Linux value, a file at 100% that keeps a floor) survives;
/// tightening is `ratchet`'s job.
let loosen (config: Config) (files: FileCoverage list) : Config =
    let lowered =
        buildFileResults config files
        |> List.filter (fun r -> not (FileResult.passed r))
        |> List.map (fun r ->
            let line = min r.LineThreshold (toThreshold r.File.LinePct)
            let branch = min r.BranchThreshold (toThreshold r.File.BranchPct)

            let updated =
                match Map.tryFind r.File.FileName config.Overrides with
                | Some existing ->
                    { existing with
                        Line = line
                        Branch = branch
                    }
                | None ->
                    {
                        Line = line
                        Branch = branch
                        Reason = Some "loosened automatically"
                        Platform = None
                    }

            r.File.FileName, updated)

    { config with
        Overrides =
            lowered
            |> List.fold (fun acc (name, ovr) -> Map.add name ovr acc) config.Overrides
    }

let loosenRaw (raw: RawConfig) (files: FileCoverage list) : RawChange =
    let resolved = resolveConfig raw
    let loosened = loosen resolved files
    mergeRawOverrides raw resolved loosened

/// One line per floor entry with a `reason` whose numbers `moves` changed, e.g.
/// `Thresholds.fs (macos): branch 91 -> 90; its reason may quote the old number`.
///
/// `ratchet`, `loosen` and `baseline-lines` keep reasons as written: only a person can
/// tell whether a number in the prose is the floor. So they say which reasons to reread.
let reasonWarnings (moves: FloorMove list) : string list =
    moves
    |> List.filter (fun m -> m.Reason.IsSome)
    |> List.groupBy (fun m -> m.File, m.Platform)
    |> List.map (fun ((file, platform), entryMoves) ->
        let label =
            match platform with
            | Some p -> sprintf "%s (%s)" file (Platform.toString p)
            | None -> file

        let described =
            entryMoves
            |> List.map (fun m -> sprintf "%s %g -> %g" m.Field m.Old m.New)
            |> String.concat ", "

        sprintf "%s: %s; its reason may quote the old number" label described)

let mergeFromCi (raw: RawConfig) (ciPlatform: Platform) (ciResults: Map<string, CiFileResult>) : RawConfig =
    let mutable result = raw.RawOverrides

    for kv in ciResults do
        let fileName = kv.Key
        let ciLine = kv.Value.Line
        let ciBranch = kv.Value.Branch

        if ciLine < raw.DefaultLine || ciBranch < raw.DefaultBranch then
            let existingEntries = Map.tryFind fileName result |> Option.defaultValue []

            let ciEntry =
                {
                    Line = ciLine
                    Branch = ciBranch
                    Reason = None
                    Platform = Some ciPlatform
                }

            let hasPlatformEntries = existingEntries |> List.exists (fun e -> e.Platform.IsSome)

            let hasNonPlatformEntry =
                existingEntries |> List.exists (fun e -> e.Platform.IsNone)

            let newEntries =
                if List.isEmpty existingEntries then
                    [ ciEntry ]
                elif hasPlatformEntries then
                    let existsForCi =
                        existingEntries |> List.exists (fun e -> e.Platform = Some ciPlatform)

                    if existsForCi then
                        // loosen-from-ci must be monotonically non-raising: a floor for a given
                        // file+platform is only ever LOWERED toward the CI-measured value, never
                        // raised above it. Taking min per-metric prevents an anti-converging update
                        // (raising a floor above what CI stably measures, which guarantees the next
                        // CI run fails). Only the matching platform entry is touched, so platform
                        // sections stay isolated.
                        existingEntries
                        |> List.map (fun e ->
                            if e.Platform = Some ciPlatform then
                                { e with
                                    Line = min e.Line ciLine
                                    Branch = min e.Branch ciBranch
                                }
                            else
                                e)
                    else
                        existingEntries @ [ ciEntry ]
                elif hasNonPlatformEntry then
                    let localEntries =
                        existingEntries
                        |> List.map (fun e ->
                            { e with
                                Platform = Some Platform.current
                            })

                    localEntries @ [ ciEntry ]
                else
                    existingEntries @ [ ciEntry ]

            result <- Map.add fileName newEntries result

    { raw with RawOverrides = result }

/// A file the reader skipped, as `check-json` records it: its base name and the
/// `ExclusionReason.describe` text.
type CiExclusion = { File: string; Reason: string }

/// What `check-json` writes for CI to upload: the platform that measured, each
/// measured file's line and branch percentage rounded down (in report order), and
/// the files the reader skipped.
type CiResults =
    {
        Platform: Platform
        Results: (string * CiFileResult) list
        Excluded: CiExclusion list
    }

module CiResults =
    /// `{"platform": "linux", "results": {"File.fs": {"line": 59, "branch": 23}}, "excluded": [{"file": "Gen.fs", "reason": "..."}]}`
    let serialize (ci: CiResults) : string =
        let results = JsonObject()

        for name, result in ci.Results do
            results.[name] <-
                JsonObject(
                    [
                        KeyValuePair("line", JsonValue.Create(result.Line) :> JsonNode)
                        KeyValuePair("branch", JsonValue.Create(result.Branch) :> JsonNode)
                    ]
                )

        let excluded = JsonArray()

        for e in ci.Excluded do
            excluded.Add(
                JsonObject(
                    [
                        KeyValuePair("file", JsonValue.Create(e.File) :> JsonNode)
                        KeyValuePair("reason", JsonValue.Create(e.Reason) :> JsonNode)
                    ]
                )
            )

        let root = JsonObject()
        root.["platform"] <- JsonValue.Create(Platform.toString ci.Platform)
        root.["results"] <- results
        root.["excluded"] <- excluded
        root.ToJsonString(jsonOptions)

    /// Read what `serialize` writes. An unknown platform reads as `Platform.current`,
    /// a missing `excluded` as none, and unknown keys are ignored.
    let parse (json: string) : CiResults =
        if String.IsNullOrWhiteSpace(json) then
            failwith
                "CI thresholds JSON is empty. Expected a coverage-thresholds artifact with shape \
                 {\"platform\":\"linux|macos|windows\",\"results\":{\"File.fs\":{\"line\":N,\"branch\":N}}}."

        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let platform =
            Platform.ofString (root.GetProperty("platform").GetString())
            |> Option.defaultValue Platform.current

        let results =
            root.GetProperty("results").EnumerateObject()
            |> Seq.map (fun prop ->
                prop.Name,
                {
                    Line = prop.Value.GetProperty("line").GetDouble()
                    Branch = prop.Value.GetProperty("branch").GetDouble()
                })
            |> Seq.toList

        let excluded =
            match root.TryGetProperty("excluded") with
            | true, list when list.ValueKind = JsonValueKind.Array ->
                list.EnumerateArray()
                |> Seq.map (fun e ->
                    {
                        File = e.GetProperty("file").GetString()
                        Reason = e.GetProperty("reason").GetString()
                    })
                |> Seq.toList
            | _ -> []

        {
            Platform = platform
            Results = results
            Excluded = excluded
        }

/// The platform and per-file results of a `check-json` artifact.
let parseCiThresholds (json: string) : Platform * Map<string, CiFileResult> =
    let ci = CiResults.parse json
    ci.Platform, Map.ofList ci.Results
