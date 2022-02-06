module Program

open System
open Serilog
open FSharp.Control
open Argu
open Types

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


let testMain _ =
    let orderInput : Types.OrderInputInfo = {
        OrderSide = Types.OrderSide.BUY
        OrderType = Types.OrderType.LIMIT
        PositionSide = Types.PositionSide.LONG
        Price = 0.3M<price>
        Quantity = 10M<qty>
        SignalId = 1212332L
        Symbol = Symbol "DOGEUSDT"
        SignalCommandId = 12332445223L
    }
    let order : Types.OrderQueryInfo = {
        Symbol  = Symbol "DOGEUSDT"
        OrderId = OrderId "11317795-6571-46a3-a05b-1537f8ffcf4b"
    }
    //Bybit.Futures.Trade.placeOrder orderInput |> Async.RunSynchronously |> ignore
    Bybit.Futures.Trade.cancelOrder order |> Async.RunSynchronously |> ignore
    0

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
        
        Log.Information ("Starting trader...")
        run (Trade.Futures.processValidSignals
                            Db.getSignalCommands
                            Db.setSignalCommandsComplete
                            Db.saveOrder
                            Db.getPositionSize)
                            placeRealOrders |> Async.Start

        // start analysers
        Strategies.FuturesPositionAnalyser.trackPositions
            Db.getPosition
            Trader.Exchanges.knownExchanges.Values
        |> Async.Start

        // Strategies.FuturesKLineAnalyser.startAnalysis
        //     Trader.Exchanges.knownExchanges.Values
        // |> Async.Start

        WebApi.run placeRealOrders // this will block

        0
    with e ->
        printfn "Unexpected exception: %A.\nApplication stopping..." e
        Log.Fatal(e, "Unexpected exception, application stopping...")
        Log.CloseAndFlush()
        Async.Sleep 500 |> Async.RunSynchronously // just to be sure logs are all flushed
        -999

