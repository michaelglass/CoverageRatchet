module CoverageRatchet.Core.Tests.RatchetTests

open Xunit
open Swensen.Unquote
open CoverageRatchet.Thresholds
open CoverageRatchet.Ratchet
open CoverageRatchet.Core.Tests.TestHelpers

[<Fact>]
let ``ratchet tightens override when coverage improves`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [
                        "Foo.fs",
                        {
                            Line = 70.0
                            Branch = 65.0
                            Reason = Some "legacy"
                            Platform = None
                        }
                    ]
        }

    let files = [ makeFile "Foo.fs" 80.0 75.0 3 4 ]
    let result = ratchet config files

    test <@ result.Overrides.["Foo.fs"].Line = 80.0 @>
    test <@ result.Overrides.["Foo.fs"].Branch = 75.0 @>

[<Fact>]
let ``ratchet floors fractional coverage to integer thresholds`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [
                        "Foo.fs",
                        {
                            Line = 70.0
                            Branch = 65.0
                            Reason = Some "legacy"
                            Platform = None
                        }
                    ]
        }

    let files = [ makeFile "Foo.fs" 80.3 75.7 3 4 ]
    let result = ratchet config files

    test <@ result.Overrides.["Foo.fs"].Line = 80.0 @>
    test <@ result.Overrides.["Foo.fs"].Branch = 75.0 @>

[<Fact>]
let ``ratchet removes override when file reaches defaults`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [
                        "Foo.fs",
                        {
                            Line = 90.0
                            Branch = 95.0
                            Reason = Some "almost there"
                            Platform = None
                        }
                    ]
        }

    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = ratchet config files

    test <@ result.Overrides.ContainsKey("Foo.fs") = false @>

[<Fact>]
let ``ratchet never lowers thresholds`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [
                        "Foo.fs",
                        {
                            Line = 80.0
                            Branch = 70.0
                            Reason = Some "legacy"
                            Platform = None
                        }
                    ]
        }

    let files = [ makeFile "Foo.fs" 60.0 50.0 1 4 ]
    let result = ratchet config files

    test <@ result.Overrides.["Foo.fs"].Line = 80.0 @>
    test <@ result.Overrides.["Foo.fs"].Branch = 70.0 @>

[<Fact>]
let ``ratchetWithStatus returns NoChanges when all thresholds met and unchanged`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [
                        "Foo.fs",
                        {
                            Line = 80.0
                            Branch = 70.0
                            Reason = Some "legacy"
                            Platform = None
                        }
                    ]
        }

    let files = [ makeFile "Foo.fs" 80.0 70.0 3 4 ]

    test
        <@
            match ratchetWithStatus config files with
            | NoChanges -> true
            | _ -> false
        @>

[<Fact>]
let ``ratchetWithStatus returns Tightened when coverage improved`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [
                        "Foo.fs",
                        {
                            Line = 70.0
                            Branch = 65.0
                            Reason = Some "legacy"
                            Platform = None
                        }
                    ]
        }

    let files = [ makeFile "Foo.fs" 85.0 80.0 3 4 ]

    test
        <@
            match ratchetWithStatus config files with
            | Tightened _ -> true
            | _ -> false
        @>

[<Fact>]
let ``ratchetWithStatus returns Failed when coverage dropped below threshold`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [
                        "Foo.fs",
                        {
                            Line = 80.0
                            Branch = 70.0
                            Reason = Some "legacy"
                            Platform = None
                        }
                    ]
        }

    let files = [ makeFile "Foo.fs" 60.0 50.0 1 4 ]

    test
        <@
            match ratchetWithStatus config files with
            | Failed _ -> true
            | _ -> false
        @>

[<Fact>]
let ``loosen sets thresholds to actual coverage`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [
                        "Foo.fs",
                        {
                            Line = 90.0
                            Branch = 85.0
                            Reason = Some "legacy"
                            Platform = None
                        }
                    ]
        }

    let files = [ makeFile "Foo.fs" 70.0 60.0 2 4 ]
    let result = loosen config files

    test <@ result.Overrides.["Foo.fs"].Line = 70.0 @>
    test <@ result.Overrides.["Foo.fs"].Branch = 60.0 @>

[<Fact>]
let ``loosen adds override for file below 100 percent with no existing override`` () =
    let files = [ makeFile "New.fs" 80.0 75.0 3 4 ]
    let result = loosen defaultsConfig files

    test <@ result.Overrides.ContainsKey("New.fs") @>
    test <@ result.Overrides.["New.fs"].Line = 80.0 @>
    test <@ result.Overrides.["New.fs"].Reason = Some "loosened automatically" @>

[<Fact>]
let ``loosen keeps the override of a file at 100 percent`` () =
    let config =
        { defaultsConfig with
            Overrides =
                Map.ofList
                    [
                        "Foo.fs",
                        {
                            Line = 90.0
                            Branch = 85.0
                            Reason = Some "was low"
                            Platform = None
                        }
                    ]
        }

    let files = [ makeFile "Foo.fs" 100.0 100.0 4 4 ]
    let result = loosen config files

    // Tightening or dropping a passing file's floor is ratchet's job.
    test <@ result = config @>

[<Fact>]
let ``ratchetRaw updates non-platform entry when no platform-specific entries exist`` () =
    let raw: RawConfig =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawCountFloors = Map.empty
            RawOverrides =
                Map.ofList
                    [
                        "Foo.fs",
                        [
                            {
                                Line = 50.0
                                Branch = 40.0
                                Reason = Some "legacy"
                                Platform = None
                            }
                        ]
                    ]
        }

    let files = [ makeFile "Foo.fs" 70.0 60.0 3 4 ]
    let result = (ratchetRaw raw files).Config
    let entries = result.RawOverrides.["Foo.fs"]

    test <@ entries.Length = 1 @>
    test <@ entries.[0].Line = 70.0 @>
    test <@ entries.[0].Branch = 60.0 @>
    test <@ entries.[0].Platform = None @>

[<Fact>]
let ``loosenRaw adds platform-agnostic entry for new file`` () =
    let raw: RawConfig =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawCountFloors = Map.empty
            RawOverrides = Map.empty
        }

    let files = [ makeFile "New.fs" 80.0 75.0 3 4 ]
    let result = (loosenRaw raw files).Config
    test <@ result.RawOverrides.ContainsKey("New.fs") @>
    let entries = result.RawOverrides.["New.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = None @>
    test <@ entries.[0].Line = 80.0 @>

[<Fact>]
let ``parseCiThresholds - parses minimal JSON format`` () =
    let json = """{"platform":"linux","results":{"Foo.fs":{"line":59,"branch":23}}}"""

    let platform, results = parseCiThresholds json
    test <@ platform = Linux @>
    test <@ results.["Foo.fs"] = { Line = 59.0; Branch = 23.0 } @>

[<Fact>]
let ``parseCiThresholds - ignores the excluded list check-json writes`` () =
    let json =
        """{"platform":"linux","results":{"Foo.fs":{"line":59,"branch":23}},"excluded":[{"file":"Gen.fs","reason":"in a directory named \"obj\""}]}"""

    let platform, results = parseCiThresholds json
    test <@ platform = Linux @>
    test <@ results = Map.ofList [ "Foo.fs", { Line = 59.0; Branch = 23.0 } ] @>

[<Fact>]
let ``parseCiThresholds - empty string raises actionable error`` () =
    let ex = Assert.ThrowsAny<exn>(fun () -> parseCiThresholds "" |> ignore)
    test <@ ex.Message.Contains("empty") @>

[<Fact>]
let ``mergeFromCi - adds new file override when CI has file below defaults`` () =
    let raw: RawConfig =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawCountFloors = Map.empty
            RawOverrides = Map.empty
        }

    let ciResults = Map.ofList [ "NewFile.fs", { Line = 80.0; Branch = 60.0 } ]
    let result = mergeFromCi raw Linux ciResults
    test <@ result.RawOverrides.ContainsKey("NewFile.fs") @>
    let entries = result.RawOverrides.["NewFile.fs"]
    test <@ entries.Length = 1 @>
    test <@ entries.[0].Platform = Some Linux @>
    test <@ entries.[0].Line = 80.0 @>

[<Fact>]
let ``mergeFromCi - skips files at or above defaults`` () =
    let raw: RawConfig =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawCountFloors = Map.empty
            RawOverrides = Map.empty
        }

    let ciResults = Map.ofList [ "Perfect.fs", { Line = 100.0; Branch = 100.0 } ]
    let result = mergeFromCi raw Linux ciResults
    test <@ result.RawOverrides.ContainsKey("Perfect.fs") = false @>

// --- count floors: ratchet raises, baseline re-baselines ---

[<Fact>]
let ``ratchetCountFloors raises a floor toward current counts`` () =
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Foo.fs", countFloor 300 10 ]
        }

    let result = ratchetCountFloors config [ makeFileWithCounts "Foo.fs" 383 400 41 60 ]

    test <@ result.CountFloors.["Foo.fs"].CoveredLines = 383 @>
    test <@ result.CountFloors.["Foo.fs"].CoveredBranches = 41 @>

[<Fact>]
let ``ratchetCountFloors NEVER lowers a floor`` () =
    // A partial (impact-filtered) run reports fewer covered lines. The floor
    // must not follow it down, or the ratchet would erase itself.
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Foo.fs", countFloor 383 41 ]
        }

    let result = ratchetCountFloors config [ makeFileWithCounts "Foo.fs" 12 400 2 60 ]

    test <@ result.CountFloors.["Foo.fs"].CoveredLines = 383 @>
    test <@ result.CountFloors.["Foo.fs"].CoveredBranches = 41 @>

[<Fact>]
let ``ratchetCountFloors does not enrol files that have no floor`` () =
    let result =
        ratchetCountFloors defaultsConfig [ makeFileWithCounts "Foo.fs" 383 400 41 60 ]

    test <@ result.CountFloors = Map.empty @>

[<Fact>]
let ``ratchetCountFloors leaves a floor alone when its file is absent from the run`` () =
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Absent.fs", countFloor 383 41 ]
        }

    let result = ratchetCountFloors config [ makeFileWithCounts "Other.fs" 10 10 0 0 ]

    test <@ result.CountFloors.["Absent.fs"].CoveredLines = 383 @>

[<Fact>]
let ``baselineCountFloors enrols every observed file`` () =
    let files =
        [
            makeFileWithCounts "Foo.fs" 383 400 41 60
            makeFileWithCounts "Bar.fs" 12 12 0 0
        ]

    let result = baselineCountFloors defaultsConfig files

    test <@ result.CountFloors.["Foo.fs"].CoveredLines = 383 @>
    test <@ result.CountFloors.["Foo.fs"].CoveredBranches = 41 @>
    test <@ result.CountFloors.["Bar.fs"].CoveredLines = 12 @>

[<Fact>]
let ``baselineCountFloors LOWERS a floor - the legitimate-deletion path`` () =
    // The refactor case: covered code was deliberately extracted or deleted, so
    // the count legitimately drops. The tool cannot detect that on its own, so a
    // human runs this and the lowered floor lands in the config diff for review.
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Foo.fs", countFloor 383 41 ]
        }

    let result = baselineCountFloors config [ makeFileWithCounts "Foo.fs" 120 130 8 10 ]

    test <@ result.CountFloors.["Foo.fs"].CoveredLines = 120 @>
    test <@ result.CountFloors.["Foo.fs"].CoveredBranches = 8 @>

[<Fact>]
let ``baselineCountFloors preserves an existing recorded reason`` () =
    let config =
        { defaultsConfig with
            CountFloors =
                Map.ofList
                    [
                        "Foo.fs",
                        {
                            CoveredLines = 383
                            CoveredBranches = 41
                            Reason = Some "logic lives in Shared.fs"
                            Platform = None
                        }
                    ]
        }

    let result = baselineCountFloors config [ makeFileWithCounts "Foo.fs" 120 130 8 10 ]

    test <@ result.CountFloors.["Foo.fs"].Reason = Some "logic lives in Shared.fs" @>

[<Fact>]
let ``baselineCountFloors leaves a floor alone when its file is absent from the run`` () =
    let config =
        { defaultsConfig with
            CountFloors = Map.ofList [ "Absent.fs", countFloor 383 41 ]
        }

    let result = baselineCountFloors config [ makeFileWithCounts "Other.fs" 10 10 0 0 ]

    test <@ result.CountFloors.["Absent.fs"].CoveredLines = 383 @>

[<Fact>]
let ``baselineCountFloorsRaw keeps other platforms' floors untouched`` () =
    let raw =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawOverrides = Map.empty
            RawCountFloors =
                Map.ofList
                    [
                        "Foo.fs",
                        [
                            {
                                CoveredLines = 100
                                CoveredBranches = 10
                                Reason = None
                                Platform = Some Platform.current
                            }
                            {
                                CoveredLines = 999
                                CoveredBranches = 99
                                Reason = None
                                Platform = Some otherPlatform
                            }
                        ]
                    ]
        }

    let result =
        (baselineCountFloorsRaw raw [ makeFileWithCounts "Foo.fs" 55 60 5 6 ]).Config

    let entries = result.RawCountFloors.["Foo.fs"]

    let mine = entries |> List.find (fun e -> e.Platform = Some Platform.current)
    let theirs = entries |> List.find (fun e -> e.Platform = Some otherPlatform)

    test <@ mine.CoveredLines = 55 @>
    test <@ theirs.CoveredLines = 999 @>

[<Fact>]
let ``ratchetRawWithStatus reports Failed when a count floor is breached`` () =
    let raw =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawOverrides = Map.empty
            RawCountFloors = Map.ofList [ "Foo.fs", [ countFloor 383 0 ] ]
        }

    // 100% line coverage, so no percentage floor can fire — only the count can.
    let result = ratchetRawWithStatus raw [ makeFileWithCounts "Foo.fs" 300 300 0 0 ]

    match result with
    | Failed(_, failedFiles) -> test <@ failedFiles = [ "Foo.fs" ] @>
    | other -> failwithf "expected Failed, got %A" other

[<Fact>]
let ``ratchetRawWithStatus is NoChanges when counts already sit at the floor`` () =
    // Positive control for the test above: the same shape must be able to pass.
    let raw =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawOverrides = Map.empty
            RawCountFloors = Map.ofList [ "Foo.fs", [ countFloor 300 0 ] ]
        }

    let result = ratchetRawWithStatus raw [ makeFileWithCounts "Foo.fs" 300 300 0 0 ]

    test <@ result = NoChanges @>

[<Fact>]
let ``baselineCountFloorsRaw beside another platform's floor records only the measured platform`` () =
    // The file has a Linux floor captured from CI and nothing for this machine. The
    // run measured THIS platform, so the floor it writes must say so: a platform-less
    // entry next to a Linux one would claim every platform except the one that was
    // actually measured.
    let theirs =
        {
            CoveredLines = 999
            CoveredBranches = 99
            Reason = Some "from CI"
            Platform = Some otherPlatform
        }

    let raw =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawOverrides = Map.empty
            RawCountFloors = Map.ofList [ "Foo.fs", [ theirs ] ]
        }

    let result =
        (baselineCountFloorsRaw raw [ makeFileWithCounts "Foo.fs" 55 60 5 6 ]).Config

    let entries = result.RawCountFloors.["Foo.fs"]

    test <@ entries.Length = 2 @>
    test <@ List.head entries = theirs @>

    let mine = entries |> List.find (fun e -> e.Platform = Some Platform.current)
    test <@ mine.CoveredLines = 55 @>

[<Fact>]
let ``baselineCountFloorsRaw on a file with no floor at all still writes it platform-less`` () =
    // Positive control for the test above: the platform tag is earned by the presence
    // of other platforms' entries, never applied by default.
    let raw =
        {
            DefaultLine = 100.0
            DefaultBranch = 100.0
            RawOverrides = Map.empty
            RawCountFloors = Map.empty
        }

    let result =
        (baselineCountFloorsRaw raw [ makeFileWithCounts "Foo.fs" 55 60 5 6 ]).Config

    test <@ result.RawCountFloors.["Foo.fs"] = [ countFloor 55 5 ] @>

// --- floor moves and reasonWarnings ---

let private ovr line branch reason platform : Override =
    {
        Line = line
        Branch = branch
        Reason = reason
        Platform = platform
    }

let private rawWith
    (overrides: (string * Override list) list)
    (countFloors: (string * CountFloor list) list)
    : RawConfig =
    {
        DefaultLine = 100.0
        DefaultBranch = 100.0
        RawOverrides = Map.ofList overrides
        RawCountFloors = Map.ofList countFloors
    }

let private move file platform field old updated reason : FloorMove =
    {
        File = file
        Platform = platform
        Field = field
        Old = old
        New = updated
        Reason = reason
    }

[<Fact>]
let ``reasonWarnings - a moved floor with a reason names the file, platform and old -> new`` () =
    test
        <@
            reasonWarnings
                [
                    move "Thresholds.fs" (Some MacOS) "branch" 91.0 90.0 (Some "Branch floor 91")
                ]
                =
                [
                    "Thresholds.fs (macos): branch 91 -> 90; its reason may quote the old number"
                ]
        @>

[<Fact>]
let ``reasonWarnings - names both numbers of one entry on one line`` () =
    let moves =
        [
            move "Foo.fs" None "line" 50.0 60.5 (Some "why")
            move "Foo.fs" None "branch" 40.0 45.0 (Some "why")
        ]

    test
        <@
            reasonWarnings moves =
                [
                    "Foo.fs: line 50 -> 60.5, branch 40 -> 45; its reason may quote the old number"
                ]
        @>

[<Fact>]
let ``reasonWarnings - nothing for a move without a reason`` () =
    test <@ reasonWarnings [ move "Foo.fs" None "line" 50.0 60.0 None ] |> List.isEmpty @>

[<Fact>]
let ``ratchetRaw - records each moved number with the entry's reason`` () =
    let raw = rawWith [ "Foo.fs", [ ovr 30.0 100.0 (Some "was 30") None ] ] []
    let change = ratchetRaw raw [ makeFile "Foo.fs" 50.0 100.0 0 0 ]

    test <@ change.Moves = [ move "Foo.fs" None "line" 30.0 50.0 (Some "was 30") ] @>
    test <@ change.Config.RawOverrides.["Foo.fs"] = [ ovr 50.0 100.0 (Some "was 30") None ] @>

[<Fact>]
let ``ratchetRaw - an unmoved floor or a removed one is not a move`` () =
    let raw =
        rawWith
            [
                "Unmoved.fs", [ ovr 50.0 50.0 (Some "why") None ]
                "Removed.fs", [ ovr 50.0 50.0 (Some "why") None ]
            ]
            []

    let change =
        ratchetRaw raw [ makeFile "Unmoved.fs" 50.0 50.0 1 2; makeFile "Removed.fs" 100.0 100.0 0 0 ]

    test <@ change.Moves |> List.isEmpty @>
    test <@ change.Removed = [ "Removed.fs" ] @>

[<Fact>]
let ``loosenRaw - moves only the entry for the running platform`` () =
    let theirs = ovr 50.0 50.0 (Some "theirs") (Some otherPlatform)

    let raw =
        rawWith [ "Foo.fs", [ theirs; ovr 60.0 60.0 (Some "mine") (Some Platform.current) ] ] []

    let change = loosenRaw raw [ makeFile "Foo.fs" 55.0 100.0 0 0 ]

    test <@ change.Moves = [ move "Foo.fs" (Some Platform.current) "line" 60.0 55.0 (Some "mine") ] @>
    test <@ change.Added |> List.isEmpty @>

[<Fact>]
let ``baselineCountFloorsRaw - records count moves and added files`` () =
    let floorWith lines reason =
        { countFloor lines 5 with
            Reason = reason
        }

    let raw =
        rawWith
            []
            [
                "Foo.fs", [ floorWith 10 (Some "10 lines") ]
                "Bar.fs", [ floorWith 10 None ]
            ]

    let change =
        baselineCountFloorsRaw
            raw
            [
                makeFileWithCounts "Foo.fs" 12 20 5 6
                makeFileWithCounts "Bar.fs" 8 20 5 6
                makeFileWithCounts "New.fs" 3 4 0 0
            ]

    test
        <@
            change.Moves =
                [
                    move "Bar.fs" None "coveredLines" 10.0 8.0 None
                    move "Foo.fs" None "coveredLines" 10.0 12.0 (Some "10 lines")
                ]
        @>

    test <@ change.Added = [ "New.fs" ] @>

    test <@ reasonWarnings change.Moves = [ "Foo.fs: coveredLines 10 -> 12; its reason may quote the old number" ] @>

// --- loosen: only failing files change ---

[<Fact>]
let ``loosen leaves a passing file's floor below its coverage as it is`` () =
    let config =
        { defaultsConfig with
            Overrides = Map.ofList [ "Linux.fs", ovr 40.0 30.0 (Some "Linux CI value") None ]
        }

    test <@ loosen config [ makeFile "Linux.fs" 80.0 90.0 9 10 ] = config @>

[<Fact>]
let ``loosen lowers only the failing number`` () =
    let config =
        { defaultsConfig with
            Overrides = Map.ofList [ "Foo.fs", ovr 80.0 90.0 (Some "why") None ]
        }

    let result = loosen config [ makeFile "Foo.fs" 70.0 95.0 19 20 ]

    test <@ result.Overrides.["Foo.fs"] = ovr 70.0 90.0 (Some "why") None @>

[<Fact>]
let ``loosen adds a floor for a failing file and none for a passing one`` () =
    let result =
        loosen defaultsConfig [ makeFile "New.fs" 50.0 100.0 0 0; makeFile "Fine.fs" 100.0 100.0 0 0 ]

    test <@ result.Overrides = Map.ofList [ "New.fs", ovr 50.0 100.0 (Some "loosened automatically") None ] @>

[<Fact>]
let ``loosenRaw keeps another platform's entry and adds this platform's for a failing file`` () =
    let linux = ovr 40.0 30.0 (Some "Linux CI value") (Some otherPlatform)

    let change =
        loosenRaw (rawWith [ "Foo.fs", [ linux ] ] []) [ makeFile "Foo.fs" 60.0 100.0 0 0 ]

    test
        <@
            change.Config.RawOverrides.["Foo.fs"] =
                [
                    linux
                    ovr 60.0 100.0 (Some "loosened automatically") (Some Platform.current)
                ]
        @>

    test <@ change.Added = [ "Foo.fs" ] @>
    test <@ change.Moves |> List.isEmpty @>
