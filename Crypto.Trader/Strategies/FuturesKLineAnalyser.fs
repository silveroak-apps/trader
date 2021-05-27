module Strategies.WarOnFuturesKLines

open AnalysisTypes
open FsToolkit.ErrorHandling
open System
open Serilog
open Strategies.Common

let getCandles (exchangeId: int64) (symbol: Symbol) =
    
    let klineTypeFor (Symbol s) =
        if s.EndsWith("USDT")
        then USDTFutures
        else CoinMFutures

    match knownMarketDataProviders.TryGetValue exchangeId with
    | false, _ -> AsyncResult.ofResult (Result.Error (KLineError.Error <| sprintf "Unknown market data provider: %d" exchangeId))
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

type private PositionSide =
| LONG
| SHORT

type private SignalAction =
| OPEN
| CLOSE

let private raiseSignal (a: SignalAction) (p: PositionSide) (candle: Analysis.HeikenAshi) =
    let isSignalInPlay () =
        AsyncResult.ofResult (Ok false)
    
    asyncResult {
        return ()
    }

let analyseCandles (haCandlesResult: Async<Result<seq<Analysis.HeikenAshi>, KLineError>>) =
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
                then raiseSignal OPEN LONG latestCandle
                elif shouldShort
                then raiseSignal OPEN SHORT latestCandle
                else AsyncResult.ofResult (Ok ())
        else
            Log.Warning ("Fewer than 3 candles so far, ignoring...")

    } |> Async.map (Result.teeError logKLineError)

let analyseHACandles (exchangeId: int64) (symbols: Symbol seq) =
    symbols
    |> Seq.distinctBy (fun (Symbol s) -> s)
    |> Seq.map (getCandles exchangeId)
    |> Seq.map (AsyncResult.map Analysis.heikenAshi)
    |> Seq.map analyseCandles
    |> Async.Parallel
    |> Async.Ignore