﻿open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text
open Argu
open FSharp.Analyzers.SDK
open GlobExpressions
open Ionide.ProjInfo

type Arguments =
    | Project of string
    | Analyzers_Path of string
    | Fail_On_Warnings of string list
    | Ignore_Files of string list
    | Verbose

    interface IArgParserTemplate with
        member s.Usage = ""

let mutable verbose = false

let createFCS () =
    let checker =
        FSharpChecker.Create(projectCacheSize = 200, keepAllBackgroundResolutions = true, keepAssemblyContents = true)
    // checker.ImplicitlyStartBackgroundWork <- true
    checker

let fcs = createFCS ()

let parser = ArgumentParser.Create<Arguments>(errorHandler = ProcessExiter())

let rec mkKn (ty: Type) =
    if Reflection.FSharpType.IsFunction(ty) then
        let _, ran = Reflection.FSharpType.GetFunctionElements(ty)
        let f = mkKn ran
        Reflection.FSharpValue.MakeFunction(ty, (fun _ -> f))
    else
        box ()

let origForegroundColor = Console.ForegroundColor

let printInfo (fmt: Printf.TextWriterFormat<'a>) : 'a =
    if verbose then
        Console.ForegroundColor <- ConsoleColor.DarkGray
        printf "Info : "
        Console.ForegroundColor <- origForegroundColor
        printfn fmt
    else
        unbox (mkKn typeof<'a>)

let printError text arg =
    Console.ForegroundColor <- ConsoleColor.Red
    printf "Error : "
    printfn text arg
    Console.ForegroundColor <- origForegroundColor

let loadProject toolsPath projPath =
    async {
        let loader = WorkspaceLoader.Create(toolsPath)
        let parsed = loader.LoadProjects [ projPath ] |> Seq.toList
        let fcsPo = FCS.mapToFSharpProjectOptions parsed.Head parsed

        return fcsPo
    }

let typeCheckFile (options: FSharpProjectOptions) (fileName: string) (sourceText: ISourceText) =
    let parseRes, checkAnswer =
        fcs.ParseAndCheckFileInProject(fileName, 0, sourceText, options)
        |> Async.RunSynchronously //ToDo: Validate if 0 is ok

    match checkAnswer with
    | FSharpCheckFileAnswer.Aborted ->
        printError "Checking of file %s aborted" fileName
        None
    | FSharpCheckFileAnswer.Succeeded result -> Some(parseRes, result)

let createContext
    (checkProjectResults: FSharpCheckProjectResults)
    (fileName: string)
    (sourceText: ISourceText)
    ((parseFileResults: FSharpParseFileResults, checkFileResults: FSharpCheckFileResults))
    : CliContext option
    =
    match checkFileResults.ImplementationFile with
    | Some tast ->
        let context: CliContext =
            {
                FileName = fileName
                SourceText = sourceText
                ParseFileResults = parseFileResults
                CheckFileResults = checkFileResults
                TypedTree = tast
                CheckProjectResults = checkProjectResults
            }

        Some context
    | _ -> None

let runProject (client: Client<CliAnalyzerAttribute, CliContext>) toolsPath proj (globs: Glob list) =
    async {
        let path = Path.Combine(Environment.CurrentDirectory, proj) |> Path.GetFullPath
        let! option = loadProject toolsPath path
        let! checkProjectResults = fcs.ParseAndCheckProject(option)

        return
            option.SourceFiles
            |> Array.filter (fun file ->
                match globs |> List.tryFind (fun g -> g.IsMatch file) with
                | Some g ->
                    printInfo $"Ignoring file %s{file} for pattern %s{g.Pattern}"
                    false
                | None -> true
            )
            |> Array.choose (fun fileName ->
                let fileContent = File.ReadAllText fileName
                let sourceText = SourceText.ofString fileContent

                typeCheckFile option fileName sourceText
                |> Option.map (createContext checkProjectResults fileName sourceText)
            )
            |> Array.collect (fun ctx ->
                match ctx with
                | Some c ->
                    printInfo "Running analyzers for %s" c.FileName
                    client.RunAnalyzers c
                | None -> failwithf "could not get context for file %s" path
            )
            |> Some
    }

let printMessages failOnWarnings (msgs: Message array) =
    if verbose then
        printfn ""

    if verbose && Array.isEmpty msgs then
        printfn "No messages found from the analyzer(s)"

    msgs
    |> Seq.iter (fun m ->
        let color =
            match m.Severity with
            | Error -> ConsoleColor.Red
            | Warning when failOnWarnings |> List.contains m.Code -> ConsoleColor.Red
            | Warning -> ConsoleColor.DarkYellow
            | Info -> ConsoleColor.Blue

        Console.ForegroundColor <- color

        printfn
            "%s(%d,%d): %s %s - %s"
            m.Range.FileName
            m.Range.StartLine
            m.Range.StartColumn
            (m.Severity.ToString())
            m.Code
            m.Message

        Console.ForegroundColor <- origForegroundColor
    )

    msgs

let calculateExitCode failOnWarnings (msgs: Message array option) : int =
    match msgs with
    | None -> -1
    | Some msgs ->
        let check =
            msgs
            |> Array.exists (fun n ->
                n.Severity = Error
                || (n.Severity = Warning && failOnWarnings |> List.contains n.Code)
            )

        if check then -2 else 0

[<EntryPoint>]
let main argv =
    let toolsPath = Init.init (DirectoryInfo Environment.CurrentDirectory) None

    let results = parser.ParseCommandLine argv
    verbose <- results.Contains <@ Verbose @>
    printInfo "Running in verbose mode"

    let failOnWarnings = results.GetResult(<@ Fail_On_Warnings @>, [])
    printInfo "Fail On Warnings: [%s]" (failOnWarnings |> String.concat ", ")

    let ignoreFiles = results.GetResult(<@ Ignore_Files @>, [])
    printInfo "Ignore Files: [%s]" (ignoreFiles |> String.concat ", ")
    let ignoreFiles = ignoreFiles |> List.map Glob

    let analyzersPath =
        let path = results.GetResult(<@ Analyzers_Path @>, "packages/Analyzers")

        if Path.IsPathRooted path then
            path
        else
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path))

    printInfo "Loading analyzers from %s" analyzersPath

    let client = Client<CliAnalyzerAttribute, CliContext>()

    let dlls, analyzers = client.LoadAnalyzers (printError "%s") analyzersPath

    printInfo "Registered %d analyzers from %d dlls" analyzers dlls

    let projOpt = results.TryGetResult <@ Project @>

    let results =
        async {
            match projOpt with
            | None ->
                printError
                    "No project given. Use `--project PATH_TO_FSPROJ`. Pass path relative to current directory.%s"
                    ""

                return None
            | Some proj ->
                let project =
                    if Path.IsPathRooted proj then
                        proj
                    else
                        Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, proj))

                let! results = runProject client toolsPath project ignoreFiles
                return results |> Option.map (printMessages failOnWarnings)
        }
        |> Async.RunSynchronously

    calculateExitCode failOnWarnings results
