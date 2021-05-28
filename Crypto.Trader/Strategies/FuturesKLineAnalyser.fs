module Strategies.FuturesKLineAnalyser

open AnalysisTypes
open FsToolkit.ErrorHandling
open System
open Serilog
open Strategies.Common
open Types

let getCandles (exchangeId: ExchangeId) (symbol: Symbol) =
    
    let klineTypeFor (Symbol s) =
        if s.EndsWith("USDT")
        then USDTFutures
        else CoinMFutures

    match knownMarketDataProviders.TryGetValue exchangeId with
    | false, _ -> AsyncResult.ofResult (Result.Error (KLineError.Error <| sprintf "Unknown market data provider: %A" exchangeId))
    | true, mp ->
        let q = {
            KLineQuery.IntervalMinutes = 1
            Limit = 5
            Symbol = symbol
            OpenTime = DateTimeOffset.UtcNow.AddMinutes -5.0
            Type = klineTypeFor symbol
        }
        mp.GetKLines q

let private logKLineError (e: KLineError) = 
    match e with
    | Error s -> s
    | InvalidResponse s -> sprintf "InvalidResponse getting klines: %s" s
    | UnsupportedKlineTypeError s -> sprintf "UnsupportedKLineTypeError: %s" s
    |> Log.Error

let private raiseEvent (exchangeId: ExchangeId) (a: SignalAction) (p: PositionSide) (candle: Analysis.HeikenAshi) =    
    asyncResult {
        Log.Information ("About to raise a market event for {Action} {PositionSide} {Symbol} on {Exchange}",
            a, p, candle.Symbol)
        let (Symbol symbol) = candle.Symbol
        let! exchange = Trader.Exchanges.lookupExchange exchangeId
        let marketEvent = {
            MarketEvent.Name = "futures_kline_war_1m"
            Price = candle.OriginalClose
            Symbol = symbol.ToUpperInvariant()
            Market = if (symbol.ToUpperInvariant()).EndsWith("PERP") then "USD" else "USDT" // hardcode for now
            TimeFrame = "1" // hardcode for now
            Exchange = exchange.Name
            Category = "futures_kline_war"
            Contracts = 0M // no contracts for now: TODO pull from config or somewhere else?
        }
        let! _ = raiseMarketEvent marketEvent
        Log.Information("Raised a market event to close the long: {MarketEvent}", marketEvent)
    } |> AsyncResult.mapError KLineError.Error

let analyseCandles (exchangeId: ExchangeId) (haCandlesResult: Async<Result<seq<Analysis.HeikenAshi>, KLineError>>) =
    asyncResult {
        let! candles = haCandlesResult

        let candleArray = Seq.toArray candles |> Array.sortByDescending (fun c -> c.OpenTime)
        
        if Array.length candleArray > 2
        then
            let fbCandle    = candleArray |> Array.map (fun c -> c.Low   = c.Open) 
            let ftCandle    = candleArray |> Array.map (fun c -> c.High  = c.Open)
            let redCandle   = candleArray |> Array.map (fun c -> c.Close < c.Open)
            let greenCandle = candleArray |> Array.map (fun c -> c.Close > c.Open)
            let high        = candleArray |> Array.map (fun c -> c.High)
            let low         = candleArray |> Array.map (fun c -> c.Low)
        
            // 0 is latest candle, 1 is previous and so on...
            let shouldLong = 
                fbCandle.[0] && fbCandle.[1] && greenCandle.[2] && (high.[0] > high.[1])

            let shouldShort =
                ftCandle.[0] && ftCandle.[1] && redCandle.[2] && (low.[0] < low.[1])

            let latestCandle = candleArray.[0]

            return!
                if shouldLong
                then raiseEvent exchangeId OPEN LONG latestCandle
                elif shouldShort
                then raiseEvent exchangeId OPEN SHORT latestCandle
                else AsyncResult.ofResult(Ok ())
        else
            Log.Warning ("Fewer than 3 candles so far, ignoring...")

    } |> Async.map (Result.teeError logKLineError)

let analyseHACandles (exchangeId: ExchangeId) (symbols: Symbol seq) =
    symbols
    |> Seq.distinctBy (fun (Symbol s) -> s)
    |> Seq.map (getCandles exchangeId)
    |> Seq.map (AsyncResult.map Analysis.heikenAshi)
    |> Seq.map (analyseCandles exchangeId)
    |> Async.Parallel
    |> Async.Ignore

let startAnalysis () =
    let nSeconds = TimeSpan.FromSeconds 60.0
    let exchanges = Trader.Exchanges.knownExchanges.Values
    let symbols = Trader.Exchanges.allSymbols |> Seq.map Symbol
    exchanges
    |> Seq.map (fun exchange ->
            repeatEvery nSeconds (fun () -> analyseHACandles exchange.Id symbols) "FuturesKLineAnalyser"
        )
    |> Async.Parallel
    |> Async.Ignore