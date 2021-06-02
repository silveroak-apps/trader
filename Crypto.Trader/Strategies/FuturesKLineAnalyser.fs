module Strategies.FuturesKLineAnalyser

open AnalysisTypes
open FsToolkit.ErrorHandling
open System
open Serilog
open Strategies.Common
open Types
open System.Diagnostics
open System.Collections.Concurrent

type private CandleKey = {
    ExchangeId: ExchangeId
    Symbol: Symbol
}

type private EventKey = {
    ExchangeId: ExchangeId
    Symbol: Symbol
    SignalAction: SignalAction
    PositionSide: PositionSide
}

let private candlesFetchTimes = new ConcurrentDictionary<CandleKey, DateTimeOffset>()
let private raisedEvents = new ConcurrentDictionary<EventKey, DateTimeOffset>()

let private getCandles (exchangeId: ExchangeId) (symbol: Symbol) =
    
    let klineTypeFor (Symbol s) =
        if s.EndsWith("USDT")
        then USDTFutures
        else CoinMFutures

    match knownMarketDataProviders.TryGetValue exchangeId with
    | false, _ -> AsyncResult.ofResult (Result.Error (KLineError.Error <| sprintf "Unknown market data provider: %A" exchangeId))
    | true, mp ->
        let q = {
            KLineQuery.IntervalMinutes = 1
            Limit = 15
            Symbol = symbol
            OpenTime = DateTimeOffset.UtcNow.AddMinutes -15.0
            Type = klineTypeFor symbol
        }
        mp.GetKLines q

let private logKLineError (e: KLineError) = 
    match e with
    | Error s -> s
    | InvalidResponse s -> sprintf "InvalidResponse getting klines: %s" s
    | UnsupportedKlineTypeError s -> sprintf "UnsupportedKLineTypeError: %s" s
    |> Log.Error

let private cleanupOldEventsInMemory () =
    let itemsToRemove =
        raisedEvents
        |> Seq.filter (fun kv -> DateTimeOffset.UtcNow - kv.Value > TimeSpan.FromHours 1.0)
        |> Seq.toList
    itemsToRemove
    |> Seq.iter (fun kv -> raisedEvents.TryRemove kv |> ignore)

let private raiseEvent (exchangeId: ExchangeId) (a: SignalAction) (p: PositionSide) (candle: Analysis.HeikenAshi) =    
    asyncResult {
        let candleDataAge = DateTimeOffset.UtcNow - candle.OpenTime.ToUniversalTime()
        let eventKey = {
                EventKey.ExchangeId = exchangeId
                SignalAction = a
                Symbol = candle.Symbol
                PositionSide = p
            }
        let candleCloseTime = candle.OpenTime.AddMinutes <| float candle.IntervalMinutes

        let recentEventToleranceMinutes = 2.0 
        let raisedSameEventRecently = 
            raisedEvents.ContainsKey(eventKey) && 
            (candleCloseTime - raisedEvents.[eventKey]) < TimeSpan.FromMinutes(float candle.IntervalMinutes * recentEventToleranceMinutes)

        let candleDataTooOld = candleDataAge > TimeSpan.FromMinutes (float candle.IntervalMinutes * 1.2)
        if candleDataTooOld
        then
            Log.Debug ("Error raising event: Candle data is out of date for exchange {Exchange}, symbol: {Symbol}. Diff: {TimeDiff}",
                    exchangeId, 
                    candle.Symbol,
                    candleDataAge)
        elif raisedSameEventRecently
        then
            Log.Debug ("Not raising event: raised an event recently at {LastTimeWeRaised} ({X} mins ago), we don't raise another one within {Y} mins.",
                raisedEvents.[eventKey],
                (candleCloseTime - raisedEvents.[eventKey]).TotalMinutes,
                recentEventToleranceMinutes)
        else
            Log.Information ("About to raise a market event for {Action} {PositionSide} {Symbol}, candle age: {CandleDataAge}: ",
                a, p, candle.Symbol,  candleDataAge)
            let (Symbol symbol) = candle.Symbol
            let! exchange = Trader.Exchanges.lookupExchange exchangeId

            let marketEvent = {
                MarketEvent.Name = sprintf "%s_%s_futures_kline_war_1m_%s" (string a) (string p) (string symbol) |> (fun s -> s.ToLowerInvariant())
                Price = candle.Original.Close
                Symbol = symbol.ToUpperInvariant()
                Market = if (symbol.ToUpperInvariant()).EndsWith("PERP") then "USD" else "USDT" // hardcode for now
                TimeFrame = "1" // hardcode for now
                Exchange = exchange.Name
                Category = sprintf "futures_kline_war_%s" (string symbol)
                Contracts = 0M // no contracts for now: TODO pull from config or somewhere else?
            }
            let! _ = raiseMarketEvent marketEvent

            cleanupOldEventsInMemory ()
            raisedEvents.[eventKey] <- candleCloseTime

            Log.Information("Raised a market event to close the long: {MarketEvent}. EventKey = {EventKey}", marketEvent, eventKey)

    } |> AsyncResult.mapError KLineError.Error

let private analyseCandles (exchangeId: ExchangeId) (haCandles: seq<Analysis.HeikenAshi>) =
    asyncResult {

        let candleArray = Seq.toArray haCandles |> Array.sortByDescending (fun c -> c.OpenTime)
        
        if Array.length candleArray > 2
        then
            let fbCandle    = candleArray |> Array.map (fun c -> c.Low   = c.Open) 
            let ftCandle    = candleArray |> Array.map (fun c -> c.High  = c.Open)
            let redCandle   = candleArray |> Array.map (fun c -> c.Close < c.Open)
            let greenCandle = candleArray |> Array.map (fun c -> c.Close > c.Open)
            let high        = candleArray |> Array.map (fun c -> c.High)
            let low         = candleArray |> Array.map (fun c -> c.Low)
            let close       = candleArray |> Array.map (fun c -> c.Close)
            let open_       = candleArray |> Array.map (fun c -> c.Open)

            // 0 is latest candle, 1 is previous and so on...
            //close > close[1] and close[1] > close[2] and fbCandle and fbCandle[1] 
            let shouldLong = 
                fbCandle.[0] && fbCandle.[1] && (close.[0] > close.[1]) && (close.[1] > close.[2])

            //open < open[1] and open[1] < open[2] and ftCandle and ftCandle[1] 
            let shouldShort =
                ftCandle.[0] && ftCandle.[1] && (close.[0] < close.[1]) && (close.[1] < close.[2])

            let latestCandle = candleArray.[0]

            Log.Debug ("Analysing HA candles for {Exchange}:{Symbol}. Open: {OpenTime}, Interval: {IntervalMinutes}. " + 
                "FB Low-Open: {FBDiff} {FB}, FT High-Open: {FTDiff} {FT}, " +
                "Colour: {Colour}",
                    exchangeId, string latestCandle.Symbol, latestCandle.OpenTime, latestCandle.IntervalMinutes,
                    latestCandle.Low - latestCandle.Open, fbCandle.[0],
                    latestCandle.High - latestCandle.Open, ftCandle.[0],
                    if greenCandle.[0] then "Green" elif redCandle.[0] then "Red" else ""
                )

            return!
                if shouldLong
                then raiseEvent exchangeId OPEN LONG latestCandle
                elif shouldShort
                then raiseEvent exchangeId OPEN SHORT latestCandle
                else AsyncResult.ofResult(Ok ())
        else
            Log.Warning ("Fewer than 3 candles so far, ignoring...")

    } |> Async.map (Result.teeError logKLineError)

let private teeUpdateFetchTime (exchangeId: ExchangeId) (symbol: Symbol) (candles: seq<KLine>) =
    let candleKey = 
        {
            CandleKey.ExchangeId = exchangeId
            Symbol = symbol
        }
    let candlesEmptyOrTooOld = 
        candles
        |> Seq.tryHead
        |> Option.map(fun candle -> 
                (DateTimeOffset.UtcNow - candle.OpenTime).TotalSeconds > 60.0
            )
        |> Option.defaultValue true
    if candlesFetchTimes.ContainsKey candleKey && candlesEmptyOrTooOld
    then
        // remove it so that we can try quickly again
        let value = candlesFetchTimes.[candleKey]
        candlesFetchTimes.TryRemove(candleKey, ref value) |> ignore
    else
        candlesFetchTimes.[candleKey] <- (candles |> Seq.head |> (fun c -> c.OpenTime))

    candles 

let private analyseHACandles (exchangeId: ExchangeId) (symbol: Symbol) =
    getCandles exchangeId symbol
    |> AsyncResult.map (teeUpdateFetchTime exchangeId symbol)
    |> AsyncResult.map Analysis.heikenAshi
    |> AsyncResult.bind (analyseCandles exchangeId)
    |> Async.Ignore

let rec private repeatEveryInterval (intervalFn: unit -> TimeSpan) (fn: unit -> Async<unit>) (nameForLogging: string)  =
    async {
        try
            let sw = Stopwatch.StartNew ()
            do! fn ()
            sw.Stop ()
            Log.Verbose ("{TimerFunctionName} took {TimerFunctionDuration} milliseconds", nameForLogging, sw.Elapsed.TotalMilliseconds)
        with e -> Log.Warning (e, "Error running function {TimerFunctionName} on timer. Continuing next time...", nameForLogging)

        let interval = intervalFn()
        Log.Verbose ("Waiting for {Interval} before another KLine fetch", interval)
        do! Async.Sleep (int interval.TotalMilliseconds)
        do! repeatEveryInterval intervalFn fn nameForLogging 
    }
    

let startAnalysis (exchanges: IFuturesExchange seq) (symbols: Symbol seq) =
    Seq.allPairs exchanges symbols
    |> Seq.map (fun (exchange, symbol) ->
            let intervalFn () =
                let candleKey = {
                    CandleKey.ExchangeId = exchange.Id
                    Symbol = symbol
                }
                match candlesFetchTimes.TryGetValue candleKey with
                | true, _ ->
                    let secondsToMinuteBoundary = DateTimeOffset.UtcNow.Second // will be between 0 and 59
                    TimeSpan.FromSeconds <| float (60 - secondsToMinuteBoundary + 1)
                | _ -> TimeSpan.FromSeconds 1.0

            repeatEveryInterval intervalFn (fun () -> analyseHACandles exchange.Id symbol) "FuturesKLineAnalyser"
        )
    |> Async.Parallel
    |> Async.Ignore