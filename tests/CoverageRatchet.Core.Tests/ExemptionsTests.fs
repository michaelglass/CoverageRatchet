module CoverageRatchet.Core.Tests.ExemptionsTests

open System
open System.IO
open Xunit
open Swensen.Unquote
open CoverageRatchet.Cobertura
open CoverageRatchet.Thresholds
open CoverageRatchet.Exemptions

/// A scratch directory, removed afterwards.
let private inTempDir (body: string -> unit) =
    let dir =
        Path.Combine(Path.GetTempPath(), "coverageratchet-exemptions-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    try
        body dir
    finally
        Directory.Delete(dir, true)

/// The source of `Queue.fs`: line 10 awaits, line 12 is the same await moved, line 20 branches.
let private source =
    [
        for n in 1..30 ->
            match n with
            | 10 -> "            let! admitted = worker.SubmitAsync(value)"
            | 20 -> "            if ready then go () else wait ()"
            | _ -> $"// line {n}"
    ]

/// A report of one file at `path` whose branch points are `(line, covered, total)`.
let private reportOf (path: string) (branches: (int * int * int) list) : Report =
    let lines =
        branches
        |> List.map (fun (n, c, t) ->
            $"""<line number="{n}" hits="1" condition-coverage="{c * 100 / t}%% ({c}/{t})" />""")
        |> String.concat "\n"

    let xml =
        $"""<?xml version="1.0" encoding="utf-8"?>
<coverage><packages><package><classes>
<class filename="{path}"><lines>
<line number="1" hits="1" />
{lines}
</lines></class>
</classes></package></packages></coverage>"""

    readReports ReaderOptions.defaults [ xml ]

let private exemption line arms anchor : BranchExemption =
    {
        Line = line
        Arms = arms
        Anchor = anchor
        Reason = "the already-finished arm of an await on a store change"
        Platform = None
    }

let private exempting (file: string) (entries: BranchExemption list) = Map.ofList [ file, entries ]

let private readerFor (dir: string) (report: Report) = sourceReader (Some dir) report

let private covered (result: Result<FileCoverage list, Refusal>) =
    match result with
    | Ok [ file ] -> file
    | other -> failwithf "expected one file's coverage, got %A" other

// --- the denominator -------------------------------------------------------------------

[<Fact>]
let ``an exempted uncovered arm leaves the denominator, so the file reads all reachable covered`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        File.WriteAllLines(path, source)
        let report = reportOf path [ 10, 1, 2; 20, 2, 2 ]

        let file =
            coverageOf (exempting "Queue.fs" [ exemption 10 1 "let! admitted" ]) (readerFor dir report) report
            |> covered

        test <@ (file.BranchesCovered, file.BranchesTotal) = (3, 3) @>
        test <@ file.BranchPct = 100.0 @>)

[<Fact>]
let ``an exempted arm a run does cover counts as covered`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        File.WriteAllLines(path, source)
        let report = reportOf path [ 10, 2, 2; 20, 1, 2 ]

        let file =
            coverageOf (exempting "Queue.fs" [ exemption 10 1 "let! admitted" ]) (readerFor dir report) report
            |> covered

        test <@ (file.BranchesCovered, file.BranchesTotal) = (3, 4) @>)

[<Fact>]
let ``uncovered arms beyond an exemption's count still count against the file`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        File.WriteAllLines(path, source)
        let report = reportOf path [ 10, 0, 2; 20, 2, 2 ]

        let file =
            coverageOf (exempting "Queue.fs" [ exemption 10 1 "let! admitted" ]) (readerFor dir report) report
            |> covered

        test <@ (file.BranchesCovered, file.BranchesTotal) = (2, 3) @>)

[<Fact>]
let ``a file with no exemption, and an exempted file the run did not measure, are left as they are`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        File.WriteAllLines(path, source)
        let report = reportOf path [ 10, 1, 2 ]

        let file =
            coverageOf (exempting "Absent.fs" [ exemption 10 1 "let! admitted" ]) (readerFor dir report) report
            |> covered

        test <@ (file.BranchesCovered, file.BranchesTotal) = (1, 2) @>)

// --- stale exemptions: hard errors -------------------------------------------------------

let private staleOf (result: Result<FileCoverage list, Refusal>) =
    match result with
    | Error(Stale(stale, [])) -> stale |> List.map (fun s -> s.File, s.Exemption.Line, s.Why)
    | other -> failwithf "expected stale exemptions, got %A" other

[<Fact>]
let ``an exemption whose line no longer holds its anchor is stale, and names where the anchor moved`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        // The await moved down two lines.
        File.WriteAllLines(
            path,
            source
            |> List.mapi (fun i text ->
                match i + 1 with
                | 10 -> "// line 10"
                | 12 -> "            let! admitted = worker.SubmitAsync(value)"
                | _ -> text)
        )

        let report = reportOf path [ 10, 1, 2 ]

        let result =
            coverageOf (exempting "Queue.fs" [ exemption 10 1 "let! admitted" ]) (readerFor dir report) report

        test <@ staleOf result = [ "Queue.fs", 10, AnchorMissing("// line 10", Some 12) ] @>
        test <@ result |> Result.mapError exitCodeOf = Error 1 @>)

[<Fact>]
let ``an exemption on a line with no branch point exempts nothing, and is stale`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        File.WriteAllLines(path, source)
        let report = reportOf path [ 20, 1, 2 ]

        let result =
            coverageOf (exempting "Queue.fs" [ exemption 10 1 "let! admitted" ]) (readerFor dir report) report

        test <@ staleOf result = [ "Queue.fs", 10, NoBranchPoint ] @>)

[<Fact>]
let ``an exemption of more arms than its line has is stale`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        File.WriteAllLines(path, source)
        let report = reportOf path [ 10, 1, 2 ]

        let result =
            coverageOf (exempting "Queue.fs" [ exemption 10 3 "let! admitted" ]) (readerFor dir report) report

        test <@ staleOf result = [ "Queue.fs", 10, MoreArmsThanBranches 2 ] @>)

// --- without the source: a refusal, never a pass -------------------------------------------

[<Fact>]
let ``exemptions on a file whose source is not here are refused, not applied`` () =
    inTempDir (fun dir ->
        // The report names a path this machine does not have (judged from another machine's run).
        let report = reportOf "/home/runner/work/repo/src/Queue.fs" [ 10, 1, 2 ]

        let result =
            coverageOf (exempting "Queue.fs" [ exemption 10 1 "let! admitted" ]) (readerFor dir report) report

        test <@ result = Error(Unsourced [ "Queue.fs" ]) @>
        test <@ result |> Result.mapError exitCodeOf = Error 2 @>)

[<Fact>]
let ``a report records the source path each file was measured from`` () =
    let report = reportOf "/repo/src/Queue.fs" [ 10, 1, 2 ]
    test <@ report.Sources = Map.ofList [ "Queue.fs", [ "/repo/src/Queue.fs" ] ] @>

// --- the floor file -------------------------------------------------------------------------

let private floorFile =
    """{
  "overrides": {
    "Queue.fs": {
      "line": 97,
      "branch": 100
    }
  },
  "branchExemptions": {
    "Queue.fs": [
      {
        "line": 10,
        "arms": 1,
        "anchor": "let! admitted",
        "reason": "the already-finished arm of an await on a store change",
        "platform": "linux"
      },
      {
        "line": 20,
        "arms": 1,
        "anchor": "if ready",
        "reason": "a branch only another platform takes"
      }
    ]
  }
}
"""

[<Fact>]
let ``exemptions are read from their own section, for the platform asked for`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(path, floorFile)

        let linux = loadFor Linux path
        let macos = loadFor MacOS path

        test
            <@
                linux |> Result.map (Map.map (fun _ es -> es |> List.map _.Line)) =
                    Ok(Map.ofList [ "Queue.fs", [ 10; 20 ] ])
            @>

        test
            <@
                macos |> Result.map (Map.map (fun _ es -> es |> List.map _.Line)) =
                    Ok(Map.ofList [ "Queue.fs", [ 20 ] ])
            @>)

[<Theory>]
[<InlineData("""{"line": 10, "arms": 1, "anchor": "x"}""", "reason")>]
[<InlineData("""{"line": 10, "arms": 1, "reason": "r"}""", "anchor")>]
[<InlineData("""{"line": 0, "arms": 1, "anchor": "x", "reason": "r"}""", "line")>]
[<InlineData("""{"line": 10, "arms": 0, "anchor": "x", "reason": "r"}""", "arms")>]
let ``an exemption missing a field it needs is an error naming the field`` (entry: string) (field: string) =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(path, $"""{{"branchExemptions": {{"Queue.fs": [ {entry} ]}}}}""")

        match loadFor Linux path with
        | Error message ->
            test <@ message.Contains "Queue.fs" @>
            test <@ message.Contains $"\"{field}\"" @>
        | Ok loaded -> failwithf "expected an error naming %s, got %A" field loaded)

[<Fact>]
let ``rewriting the floors keeps the exemptions section byte for byte`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(path, floorFile)
        let raw = loadRawConfig path

        let tightened =
            { raw with
                RawOverrides =
                    raw.RawOverrides
                    |> Map.map (fun _ entries -> entries |> List.map (fun o -> { o with Line = 98.0 }))
            }

        saveRawConfig path tightened

        let section (text: string) =
            let start = text.IndexOf "\"branchExemptions\""
            text.Substring start

        test <@ section (File.ReadAllText path) = section floorFile @>
        test <@ (loadRawConfig path).RawOverrides.["Queue.fs"].Head.Line = 98.0 @>)

// --- reading the floor file: every shape --------------------------------------------------

let private loadText (text: string) =
    let mutable loaded = Ok Map.empty

    inTempDir (fun dir ->
        let path = Path.Combine(dir, "coverage-ratchet.json")
        File.WriteAllText(path, text)
        loaded <- loadFor Linux path)

    loaded

[<Fact>]
let ``no floor file, an empty one, or one without the section, means no exemptions`` () =
    inTempDir (fun dir -> test <@ loadFor Linux (Path.Combine(dir, "absent.json")) = Ok Map.empty @>)
    test <@ loadText "   " = Ok Map.empty @>
    test <@ loadText """{"overrides": {}}""" = Ok Map.empty @>

[<Fact>]
let ``the section must map files to exemptions`` () =
    match loadText """{"branchExemptions": [1, 2]}""" with
    | Error message -> test <@ message.Contains "must map each file to a list of exemptions" @>
    | other -> failwithf "expected an error, got %A" other

[<Fact>]
let ``a file's single exemption may be an object rather than a list`` () =
    let loaded =
        loadText """{"branchExemptions": {"Queue.fs": {"line": 10, "arms": 1, "anchor": "x", "reason": "r"}}}"""

    test
        <@ loaded |> Result.map (Map.map (fun _ es -> es |> List.map _.Line)) = Ok(Map.ofList [ "Queue.fs", [ 10 ] ]) @>

[<Fact>]
let ``a file whose exemptions are all for another platform has none here`` () =
    let loaded =
        loadText
            """{"branchExemptions": {"Queue.fs": [{"line": 10, "arms": 1, "anchor": "x", "reason": "r", "platform": "macos"}]}}"""

    test <@ loaded = Ok Map.empty @>

[<Theory>]
[<InlineData("""{"line": "10", "arms": 1, "anchor": "x", "reason": "r"}""", "line")>]
[<InlineData("""{"line": 10, "arms": 1, "anchor": 5, "reason": "r"}""", "anchor")>]
[<InlineData("""{"line": 10, "arms": 1, "anchor": "x", "reason": "r", "platform": "mars"}""", "platform")>]
[<InlineData("""{"line": 10, "arms": 1, "anchor": "x", "reason": "r", "platform": 3}""", "platform")>]
let ``an exemption field of the wrong kind is an error naming the field`` (entry: string) (field: string) =
    match loadText $"""{{"branchExemptions": {{"Queue.fs": [ {entry} ]}}}}""" with
    | Error message -> test <@ message.Contains $"\"{field}\"" @>
    | Ok loaded -> failwithf "expected an error naming %s, got %A" field loaded

[<Fact>]
let ``one malformed exemption among good ones is an error, wherever it sits`` () =
    let good = """{"line": 10, "arms": 1, "anchor": "x", "reason": "r"}"""
    let bad = """{"line": 10, "arms": 1, "anchor": "x"}"""

    for entries in [ $"{good}, {bad}"; $"{bad}, {good}" ] do
        match loadText $"""{{"branchExemptions": {{"Queue.fs": [ {entries} ]}}}}""" with
        | Error message -> test <@ message.Contains "\"reason\"" @>
        | Ok loaded -> failwithf "expected an error, got %A" loaded

    // A second file after a malformed one does not hide it.
    match loadText $"""{{"branchExemptions": {{"A.fs": [ {bad} ], "B.fs": [ {good} ]}}}}""" with
    | Error message -> test <@ message.Contains "A.fs" @>
    | Ok loaded -> failwithf "expected an error, got %A" loaded

[<Fact>]
let ``load reads the running platform's exemptions`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "coverage-ratchet.json")

        File.WriteAllText(
            path,
            """{"branchExemptions": {"Queue.fs": {"line": 10, "arms": 1, "anchor": "x", "reason": "r"}}}"""
        )

        test <@ load path = loadFor Platform.current path @>)

// --- finding the source -----------------------------------------------------------------------

[<Fact>]
let ``a relative source path is read under the floor file's directory`` () =
    inTempDir (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
        File.WriteAllLines(Path.Combine(dir, "src", "Queue.fs"), source)
        let report = reportOf "src/Queue.fs" [ 10, 1, 2 ]

        let file =
            coverageOf (exempting "Queue.fs" [ exemption 10 1 "let! admitted" ]) (readerFor dir report) report
            |> covered

        test <@ file.BranchPct = 100.0 @>)

[<Fact>]
let ``a base name the report recorded under two paths, or never, has no readable source`` () =
    let one = reportOf "/a/Queue.fs" [ 10, 1, 2 ]
    let other = reportOf "/b/Queue.fs" [ 10, 1, 2 ]

    let both =
        { one with
            Sources = Map.ofList [ "Queue.fs", [ "/a/Queue.fs"; "/b/Queue.fs" ] ]
            Lines = one.Lines @ other.Lines
        }

    test <@ sourceReader None both "Queue.fs" = None @>
    test <@ sourceReader None one "Other.fs" = None @>

// --- edges of a line ------------------------------------------------------------------------

[<Fact>]
let ``an exemption past the end of its file is stale, the line reading as past the end`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        File.WriteAllLines(path, source)
        let report = reportOf path [ 10, 1, 2 ]

        let result =
            coverageOf (exempting "Queue.fs" [ exemption 99 1 "nowhere" ]) (readerFor dir report) report

        test <@ staleOf result = [ "Queue.fs", 99, AnchorMissing("(past the end of the file)", None) ] @>)

[<Fact>]
let ``an exempted file whose every arm is exempted reads 100 percent, with no branch left`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        File.WriteAllLines(path, source)
        let report = reportOf path [ 10, 0, 2 ]

        let file =
            coverageOf (exempting "Queue.fs" [ exemption 10 2 "let! admitted" ]) (readerFor dir report) report
            |> covered

        test <@ (file.BranchesCovered, file.BranchesTotal, file.BranchPct) = (0, 0, 100.0) @>)

[<Fact>]
let ``a stale file and an unsourced one are both reported, as stale`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        File.WriteAllLines(path, source)
        let here = reportOf path [ 10, 1, 2 ]
        let elsewhere = reportOf "/home/runner/work/repo/src/Gone.fs" [ 10, 1, 2 ]

        let report =
            { here with
                Lines = here.Lines @ elsewhere.Lines
                Sources = Map.fold (fun acc k v -> Map.add k v acc) here.Sources elsewhere.Sources
            }

        let exemptions =
            Map.ofList
                [
                    "Queue.fs", [ exemption 20 1 "let! admitted" ]
                    "Gone.fs", [ exemption 10 1 "x" ]
                ]

        match coverageOf exemptions (readerFor dir report) report with
        | Error(Stale([ stale ], [ "Gone.fs" ]) as refusal) ->
            test <@ stale.Exemption.Line = 20 @>
            test <@ exitCodeOf refusal = 1 @>
        | other -> failwithf "expected one stale and one unsourced file, got %A" other)

// --- what a person reads ----------------------------------------------------------------------

[<Fact>]
let ``each refusal reads as one line naming the file, the line and what to do`` () =
    let e = exemption 10 1 "let! admitted"

    let stale why =
        {
            File = "Queue.fs"
            Exemption = e
            Why = why
        }

    let lines =
        describe (
            Stale(
                [
                    stale NoBranchPoint
                    stale (MoreArmsThanBranches 2)
                    stale (AnchorMissing("// line 10", Some 12))
                    stale (AnchorMissing("// line 10", None))
                ],
                [ "Gone.fs" ]
            )
        )

    test <@ lines.Length = 5 @>

    test
        <@
            lines
            |> List.take 4
            |> List.forall (fun l -> l.StartsWith "stale exemption Queue.fs:10")
        @>

    test <@ lines.[0].Contains "exempts nothing" @>
    test <@ lines.[1].Contains "it exempts 1 arm(s), the line has 2" @>
    test <@ lines.[2].Contains "the anchor is on line 12" @>
    test <@ lines.[3].Contains "no single other line holds the anchor" @>
    test <@ lines.[4].Contains "exemptions on Gone.fs not applied" @>
    test <@ describe (Unsourced [ "Gone.fs" ]) = [ lines.[4] ] @>

[<Fact>]
let ``an anchor that moved to several lines names none of them`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")

        File.WriteAllLines(
            path,
            source
            |> List.mapi (fun i text ->
                match i + 1 with
                | 10 -> "// line 10"
                | 12
                | 14 -> "            let! admitted = worker.SubmitAsync(value)"
                | _ -> text)
        )

        let report = reportOf path [ 10, 1, 2 ]

        let result =
            coverageOf (exempting "Queue.fs" [ exemption 10 1 "let! admitted" ]) (readerFor dir report) report

        test <@ staleOf result = [ "Queue.fs", 10, AnchorMissing("// line 10", None) ] @>)

[<Fact>]
let ``an exemption on a file emptied of every line is stale`` () =
    inTempDir (fun dir ->
        let path = Path.Combine(dir, "Queue.fs")
        File.WriteAllText(path, "")
        let report = reportOf path [ 10, 1, 2 ]

        let result =
            coverageOf (exempting "Queue.fs" [ exemption 10 1 "let! admitted" ]) (readerFor dir report) report

        test <@ staleOf result = [ "Queue.fs", 10, AnchorMissing("(past the end of the file)", None) ] @>)
