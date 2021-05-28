module Program

open System
open Serilog
open FSharp.Control
open Argu

let configureLogging () = 
    Log.Logger <- LoggerConfiguration()
                        .Enrich.FromLogContext()
                        .Enrich.WithProperty("Application", "Trader")
                        .ReadFrom.Configuration(appConfig)
                        .CreateLogger()

type CliArgs = 
    | RealOrders
with
    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | RealOrders   -> "Runs the app in live mode with real orders."

let contains x = List.exists ((=) x)

let run processValidSignals placeRealOrders =
        let nSeconds = TimeSpan.FromSeconds 3.0
        startHeartbeat "Trader"
        repeatEvery nSeconds (fun _ -> processValidSignals placeRealOrders) "Trader" // buy and sell

[<EntryPoint>]
let main (argv: string[]) =
    try
        configureLogging ()
        Log.Information("Starting Crypto Trader")
        DapperInfra.setup ()

        let parser = ArgumentParser.Create<CliArgs>(programName = "dotnet crypto.trader.dll")
        let cliArgs = parser.Parse argv |> (fun r -> r.GetAllResults())
        let placeRealOrders = cliArgs |> contains RealOrders

        use _p = Context.LogContext.PushProperty("PlaceRealOrders", placeRealOrders)
        Log.Information ("Setting: Place real orders = {PlaceRealOrders}", placeRealOrders)
        
        Log.Information ("Starting futures trader...")
        run (Trade.Futures.processValidSignals
                            Db.getFuturesSignalCommands
                            Db.setSignalCommandsComplete
                            Db.getExchangeOrder
                            Db.saveOrder
                            Db.getPositionSize)
                            placeRealOrders |> Async.Start

        // Log.Information ("Starting spot trader...")
        // run Trade.Spot.processValidSignals placeRealOrders |> Async.Start

        // start analysers
        Strategies.FuturesPositionAnalyser.trackPositions
            Trader.Exchanges.knownExchanges.Values
            Trader.Exchanges.allSymbols
        |> Async.Start

        Strategies.FuturesKLineAnalyser.startAnalysis ()
        |> Async.Start

        WebApi.run placeRealOrders // this will block

        0
    with e ->
        printfn "Unexpected exception: %A.\nApplication stopping..." e
        Log.Fatal(e, "Unexpected exception, application stopping...")
        Log.CloseAndFlush()
        Async.Sleep 500 |> Async.RunSynchronously // just to be sure logs are all flushed
        -999
