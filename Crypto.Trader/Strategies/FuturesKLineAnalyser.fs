module Strategies.FuturesKLineAnalyser

open AnalysisTypes
open FsToolkit.ErrorHandling
open System
open Serilog
open Strategies.Common
open Types
open System.Diagnostics
open System.Collections.Concurrent
open Serilog.Context

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
            Limit = 99
            Symbol = symbol
            OpenTime = DateTimeOffset.UtcNow.AddMinutes -99.0
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
                TimeFrame = candle.IntervalMinutes
                Exchange = exchange.Name
                Category = sprintf "futures_kline_war_%s" (string symbol)
                Contracts = 0M // no contracts for now: TODO pull from config or somewhere else?
            }
            let! _ = raiseMarketEvent marketEvent

            cleanupOldEventsInMemory ()
            raisedEvents.[eventKey] <- candleCloseTime

            Log.Information("Raised a market event to close the long: {MarketEvent}. EventKey = {EventKey}", marketEvent, eventKey)

    } |> AsyncResult.mapError KLineError.Error

let private analyseCandles (exchangeId: ExchangeId) (haCandles: Analysis.HeikenAshi seq) =
    asyncResult {
        let candleArray = haCandles |> Seq.sortByDescending (fun c -> c.OpenTime) |> Seq.toArray

        if Array.length candleArray > 2 // we need atleast 3 candles to analyse
        then
            // 0 is latest candle, 1 is previous and so on...

            let latestCandle = candleArray.[0] // this could be a live candle if the close time hasn't passed
            let isLatestCandleLive = 
                let closeTime = latestCandle.OpenTime.AddMinutes <| float latestCandle.IntervalMinutes
                DateTimeOffset.UtcNow < closeTime.ToUniversalTime ()

            let latestClosedCandle = if isLatestCandleLive then candleArray.[1] else latestCandle
            let previousClosedCandle = if isLatestCandleLive then candleArray.[2] else candleArray.[1]
            let previousMinusOneClosedCandle = if isLatestCandleLive then candleArray.[3] else candleArray.[2]

            let isFlatBottom (c: Analysis.HeikenAshi) = c.Open = c.Low
            let isFlatTop (c: Analysis.HeikenAshi) = c.Open = c.High
            let increasingClosePrice (older: Analysis.HeikenAshi) (newer: Analysis.HeikenAshi) = newer.Close > older.Close
            let increasingHighPrice (prev: Analysis.HeikenAshi) (next: Analysis.HeikenAshi) = next.High > prev.High
            let decreasingClosePrice (older: Analysis.HeikenAshi) (newer: Analysis.HeikenAshi) = newer.Close < older.Close
            let decreasingLowPrice (older: Analysis.HeikenAshi) (newer: Analysis.HeikenAshi) = newer.Low < older.Low

            (*
                open long: 
                    two most recent closed candles -->
                    - need to be FB, and 
                    - increasing close price, and
                    // - increasing high price
            *)
            let shouldOpenLong =
                let twoPreviousFBCandles = isFlatBottom latestClosedCandle && isFlatBottom previousClosedCandle
                let twoIncreasingCloses = 
                    increasingClosePrice previousClosedCandle latestClosedCandle &&
                    increasingClosePrice previousMinusOneClosedCandle previousClosedCandle
                let twoIncreasingHighs =
                    increasingHighPrice previousClosedCandle latestClosedCandle && 
                    increasingHighPrice previousMinusOneClosedCandle previousClosedCandle
                    
                twoPreviousFBCandles && twoIncreasingCloses && twoIncreasingHighs

            (*
                open short:
                    two most recent closed candles --> 
                    - need to be FT, and
                    - decreasing close price, and
                    // - decreasing low price
            *)
            let shouldOpenShort =
                let twoPreviousFTCandles = isFlatTop latestClosedCandle && isFlatTop previousClosedCandle
                let twoDecreasingCloses = 
                    decreasingClosePrice previousClosedCandle latestClosedCandle &&
                    decreasingClosePrice previousMinusOneClosedCandle previousClosedCandle
                let twoDecreasingLows = 
                    decreasingLowPrice previousClosedCandle latestClosedCandle &&
                    decreasingLowPrice previousMinusOneClosedCandle previousClosedCandle

                twoPreviousFTCandles && twoDecreasingCloses && twoDecreasingLows

            Log.Debug ("Analysing HA candles for {Exchange}:{Symbol}. Open: {OpenTime}, Interval: {IntervalMinutes}. " + 
                "FB Low-Open: {FBDiff} {FB}, FT High-Open: {FTDiff} {FT}, " +
                "Colour: {LatestClosedCandleColour}, " + 
                "IsLatestCandleLive: {IsLatestCandleLive}, " +
                "LatestClosedCandle: {LatestClosedCandle}",
                    exchangeId, string latestCandle.Symbol, latestCandle.OpenTime, latestCandle.IntervalMinutes,
                    latestClosedCandle.Low - latestClosedCandle.Open, isFlatBottom latestClosedCandle,
                    latestClosedCandle.High - latestClosedCandle.Open, isFlatTop latestClosedCandle,
                    (if latestClosedCandle.Close > latestClosedCandle.Open then "Green" elif latestClosedCandle.Close < latestClosedCandle.Open then "Red" else "Flat"),
                    isLatestCandleLive,
                    latestClosedCandle
                )

            return!
                if shouldOpenLong
                then raiseEvent exchangeId OPEN LONG latestCandle
                elif shouldOpenShort
                then raiseEvent exchangeId OPEN SHORT latestCandle
                else AsyncResult.ofResult(Ok ())
        else
            Log.Warning ("Fewer than 3 candles so far from {ExchangeId}, ignoring...", exchangeId)

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
        let fetchTime = 
            if not (candles |> Seq.isEmpty)
            then
                candles
                |> Seq.maxBy (fun c -> c.OpenTime) // we need newest candle to see what its open time is.
                |> (fun c -> c.OpenTime)
            else DateTimeOffset.MinValue

        Log.Debug ("Storing fetch time for key {CandleKey}: {Time}", candleKey, fetchTime)
        candlesFetchTimes.[candleKey] <- fetchTime

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
            use _ = LogContext.PushProperty("Function", nameForLogging)
            let sw = Stopwatch.StartNew ()
            do! fn ()
            sw.Stop ()
            Log.Verbose ("{TimerFunctionName} took {TimerFunctionDuration} milliseconds", nameForLogging, sw.Elapsed.TotalMilliseconds)
        with e -> Log.Warning (e, "Error running function {TimerFunctionName} on timer. Continuing next time...", nameForLogging)

        let interval = intervalFn()
        Log.Debug ("Waiting for {Interval} before another KLine fetch", interval)
        do! Async.Sleep (int interval.TotalMilliseconds)
        do! repeatEveryInterval intervalFn fn nameForLogging 
    }

let startAnalysis (exchanges: IFuturesExchange seq) =
    exchanges
    |> Seq.collect (fun exchange -> exchange.GetSupportedSymbols().Keys |> Seq.map (fun s -> (exchange, s)))
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

            repeatEveryInterval intervalFn (fun () -> analyseHACandles exchange.Id symbol) (sprintf "FuturesKLineAnalyser-%s-%s" exchange.Name (symbol.ToString()))
        )
    |> Async.Parallel
    |> Async.Ignore