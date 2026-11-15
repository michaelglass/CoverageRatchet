/// Branch exemptions: branch points a floor file names as known-unreachable, so a file's
/// branch percentage can read "all reachable branches covered".
///
/// Some branch arms cannot be reached deterministically. On Linux, every `let!`/`do!` in
/// an F# `task` is a branch on whether the awaited task had already finished, and for an
/// await on work another thread publishes after the caller suspends, the finished arm is
/// essentially never taken. A file-wide percentage absorbs such an arm, and the floor then
/// equals the worst case with no headroom: the next such arm reds the check, and lowering
/// the floor to absorb it would also hide a reachable branch lost. An exemption takes the
/// arm out of the denominator instead, by line, with a reason.
///
/// Lines move, so each exemption carries an `anchor`, text its line must hold. An
/// exemption whose line no longer holds it, that names a line with no branch point, or that
/// exempts more arms than the line has, is STALE: a hard error, never silently applied or
/// ignored. Checking an anchor needs the source, so exemptions on a file whose source this
/// machine cannot read are REFUSED: the run cannot answer, which is not a pass.
module CoverageRatchet.Exemptions

open System
open System.IO
open System.Text.Json
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds

// sync:branch-exemption:start
/// A known-unreachable branch point: up to `Arms` of the branch arms on `Line` may go
/// uncovered without counting against the file, while `Line` holds `Anchor`.
type BranchExemption =
    {
        /// The 1-based source line of the branch point.
        Line: int
        /// How many of its arms may go uncovered (at least 1).
        Arms: int
        /// Text the line must hold, so an exemption cannot follow a moved line silently.
        Anchor: string
        /// Why the arms cannot be reached. Required.
        Reason: string
        /// The platform it applies on; `None` applies on every platform.
        Platform: Platform option
    }
// sync:branch-exemption:end

/// The section of a floor file exemptions are read from. Its own section, so a floor file
/// written before exemptions existed never reads one, and the floor writer, which edits
/// only `overrides` and `countFloors`, carries it through unchanged.
[<Literal>]
let Section = "branchExemptions"

let private parseEntry (file: string) (el: JsonElement) : Result<BranchExemption, string> =
    let problem (field: string) (rule: string) =
        Error(sprintf "%s.%s: an exemption needs \"%s\" %s" Section file field rule)

    let positive (field: string) =
        match el.TryGetProperty(field) with
        | true, v when v.ValueKind = JsonValueKind.Number && v.GetInt32() >= 1 -> Ok(v.GetInt32())
        | _ -> problem field "(a whole number, at least 1)"

    let text (field: string) =
        match el.TryGetProperty(field) with
        | true, v when
            v.ValueKind = JsonValueKind.String
            && not (String.IsNullOrWhiteSpace(v.GetString()))
            ->
            Ok(v.GetString())
        | _ -> problem field "(non-empty text)"

    let platform =
        match el.TryGetProperty("platform") with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match Platform.ofString (v.GetString()) with
            | Some p -> Ok(Some p)
            | None -> problem "platform" "(macos, linux or windows)"
        | true, _ -> problem "platform" "(macos, linux or windows)"
        | false, _ -> Ok None

    match positive "line", positive "arms", text "anchor", text "reason", platform with
    | Ok line, Ok arms, Ok anchor, Ok reason, Ok platform ->
        Ok
            {
                Line = line
                Arms = arms
                Anchor = anchor
                Reason = reason
                Platform = platform
            }
    | Error e, _, _, _, _
    | _, Error e, _, _, _
    | _, _, Error e, _, _
    | _, _, _, Error e, _
    | _, _, _, _, Error e -> Error e

/// The exemptions a floor file's document `root` gives `platform`, by file.
let private exemptionsIn
    (platform: Platform)
    (path: string)
    (root: JsonElement)
    : Result<Map<string, BranchExemption list>, string> =
    match root.TryGetProperty(Section) with
    | false, _ -> Ok Map.empty
    | true, section when section.ValueKind <> JsonValueKind.Object ->
        Error(sprintf "%s: \"%s\" must map each file to a list of exemptions" path Section)
    | true, section ->
        section.EnumerateObject()
        |> Seq.map (fun file ->
            let entries =
                if file.Value.ValueKind = JsonValueKind.Array then
                    file.Value.EnumerateArray() |> Seq.toList
                else
                    [ file.Value ]

            entries
            |> List.map (parseEntry file.Name)
            |> List.fold
                (fun acc entry ->
                    match acc, entry with
                    | Ok parsed, Ok e -> Ok(e :: parsed)
                    | Error e, _
                    | _, Error e -> Error e)
                (Ok [])
            |> Result.map (fun parsed ->
                file.Name,
                parsed
                |> List.rev
                |> List.filter (fun e -> e.Platform = None || e.Platform = Some platform)))
        |> Seq.fold
            (fun acc file ->
                match acc, file with
                | Ok files, Ok(_, []) -> Ok files
                | Ok files, Ok(name, entries) -> Ok(Map.add name entries files)
                | Error e, _
                | _, Error e -> Error e)
            (Ok Map.empty)
        |> Result.mapError (fun message -> path + ": " + message)

/// The exemptions the floor file at `path` gives `platform`, by file: every entry naming
/// that platform or none. A missing file or section is no exemptions; a malformed entry is
/// an error naming its file and field.
let loadFor (platform: Platform) (path: string) : Result<Map<string, BranchExemption list>, string> =
    let text = if File.Exists(path) then File.ReadAllText(path) else ""

    if String.IsNullOrWhiteSpace(text) then
        Ok Map.empty
    else
        // Disposed explicitly: `use` would add a null check on a document that is never null.
        let doc = JsonDocument.Parse(text)

        try
            exemptionsIn platform path doc.RootElement
        finally
            doc.Dispose()

/// The exemptions the floor file at `path` gives the running platform.
let load (path: string) : Result<Map<string, BranchExemption list>, string> = loadFor Platform.current path

// sync:exemption-problems:start
/// Why an exemption no longer describes its line.
type StaleReason =
    /// The line has no branch point in the report: the exemption exempts nothing.
    | NoBranchPoint
    /// The exemption allows more uncovered arms than the line has.
    | MoreArmsThanBranches of total: int
    /// The line no longer holds the anchor; `movedTo` is the one other line that does.
    | AnchorMissing of actual: string * movedTo: int option

type StaleExemption =
    {
        File: string
        Exemption: BranchExemption
        Why: StaleReason
    }

/// Why a run's exemptions could not be applied.
type Refusal =
    /// Some exemptions no longer describe their lines (and, alongside, the files whose
    /// source could not be read). Something a person changed: exit 1.
    | Stale of stale: StaleExemption list * unsourced: string list
    /// Exempted files whose source this machine cannot read, so no anchor can be checked.
    /// The run cannot answer, which is not a pass: exit 2.
    | Unsourced of files: string list
// sync:exemption-problems:end

/// The process exit code for a refusal: 1 for a stale exemption, 2 for a run that cannot
/// check its exemptions (as `Thresholds.exitCodeOf` separates a fall from an unknown).
let exitCodeOf (refusal: Refusal) : int =
    match refusal with
    | Stale _ -> 1
    | Unsourced _ -> 2

/// Reads a file's source by base name, from the one path `report` recorded it under: as
/// recorded when that path exists, else, for a relative path, under `root`. `None` when it
/// cannot be found, or when the report recorded two different paths for the one base name.
let sourceReader (root: string option) (report: Report) : string -> string array option =
    let resolve (recorded: string) =
        let candidates =
            [
                yield recorded
                match root with
                | Some dir when not (Path.IsPathRooted recorded) -> yield Path.Combine(dir, recorded)
                | _ -> ()
            ]

        candidates |> List.tryFind File.Exists

    fun baseName ->
        match Map.tryFind baseName report.Sources with
        | Some [ recorded ] -> resolve recorded |> Option.map File.ReadAllLines
        | _ -> None

let private staleness (lines: string array) (points: Map<int, int * int>) (e: BranchExemption) =
    // `n` is an exemption's line (parsed at least 1) or one of 1..lines.Length.
    let holds (n: int) =
        n <= lines.Length && lines.[n - 1].Contains(e.Anchor, StringComparison.Ordinal)

    if not (holds e.Line) then
        let actual =
            if e.Line <= lines.Length then
                lines.[e.Line - 1].Trim()
            else
                "(past the end of the file)"

        let movedTo =
            match [ 1 .. lines.Length ] |> List.filter holds with
            | [ only ] -> Some only
            | _ -> None

        Some(AnchorMissing(actual, movedTo))
    else
        match Map.tryFind e.Line points with
        | None -> Some NoBranchPoint
        | Some(_, total) when e.Arms > total -> Some(MoreArmsThanBranches total)
        | Some _ -> None

/// One file's coverage with its exempted uncovered arms out of the denominator.
let private exempted (file: FileCoverage) (points: Map<int, int * int>) (exemptions: BranchExemption list) =
    let removed =
        exemptions
        |> List.sumBy (fun e ->
            let covered, total = points.[e.Line]
            min e.Arms (total - covered))

    let total = file.BranchesTotal - removed

    { file with
        BranchesTotal = total
        BranchPct =
            if total > 0 then
                float file.BranchesCovered / float total * 100.0
            else
                100.0
    }

/// What became of one measured file's exemptions.
type private Judgement =
    | Applied of FileCoverage
    | StaleIn of StaleExemption list
    | NoSource

/// `report`'s coverage, each file's exempted uncovered arms out of its branch denominator,
/// or why the exemptions could not be applied. Only files the report measured are checked:
/// an exempted file a partial run did not measure is left to the floors' own obligation.
let coverageOf
    (exemptions: Map<string, BranchExemption list>)
    (readSource: string -> string array option)
    (report: Report)
    : Result<FileCoverage list, Refusal> =
    let files = buildCoverage report.Lines
    let byFile = report.Lines |> List.groupBy (fun r -> r.FileName) |> Map.ofList

    let judge (file: FileCoverage) (entries: BranchExemption list) =
        let points = branchPointsOf byFile.[file.FileName]

        match readSource file.FileName with
        | None -> NoSource
        | Some lines ->
            match
                entries
                |> List.choose (fun e ->
                    staleness lines points e
                    |> Option.map (fun why ->
                        {
                            File = file.FileName
                            Exemption = e
                            Why = why
                        }))
            with
            | [] -> Applied(exempted file points entries)
            | stale -> StaleIn stale

    let judged =
        files
        |> List.map (fun file ->
            match Map.tryFind file.FileName exemptions with
            | Some entries -> file.FileName, judge file entries
            | None -> file.FileName, Applied file)

    // One pass, each file into the list its judgement belongs to.
    let applied, stale, unsourced =
        List.foldBack
            (fun (name, judgement) (applied, stale, unsourced) ->
                match judgement with
                | Applied file -> file :: applied, stale, unsourced
                | StaleIn s -> applied, s @ stale, unsourced
                | NoSource -> applied, stale, name :: unsourced)
            judged
            ([], [], [])

    match stale, unsourced with
    | [], [] -> Ok applied
    | [], unsourced -> Error(Unsourced unsourced)
    | stale, unsourced -> Error(Stale(stale, unsourced))

/// One line per problem, for a person to act on.
let describe (refusal: Refusal) : string list =
    let staleLine (s: StaleExemption) =
        let at = sprintf "%s:%d (anchor %A)" s.File s.Exemption.Line s.Exemption.Anchor

        match s.Why with
        | NoBranchPoint -> sprintf "stale exemption %s: the line has no branch point, so it exempts nothing" at
        | MoreArmsThanBranches total ->
            sprintf "stale exemption %s: it exempts %d arm(s), the line has %d" at s.Exemption.Arms total
        | AnchorMissing(actual, Some moved) ->
            sprintf "stale exemption %s: the line now reads %A; the anchor is on line %d" at actual moved
        | AnchorMissing(actual, None) ->
            sprintf "stale exemption %s: the line now reads %A, and no single other line holds the anchor" at actual

    let unsourcedLine (file: string) =
        sprintf
            "exemptions on %s not applied: its source cannot be read here, so no anchor can be checked (the run cannot answer)"
            file

    match refusal with
    | Stale(stale, unsourced) -> (stale |> List.map staleLine) @ (unsourced |> List.map unsourcedLine)
    | Unsourced files -> files |> List.map unsourcedLine
