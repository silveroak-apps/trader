module Strategies.FuturesPositionAnalyser

open System
open Serilog
open FSharpx.Control
open Serilog.Context
open System.Collections.Generic
open System.Collections.Concurrent
open FSharp.Control
open FsToolkit.ErrorHandling
open Types
open Trader.Exchanges
open Strategies.Common
open DbTypes
type PositionKey = PositionKey of string

type PositionAnalysis = {
    ExchangeId: ExchangeId
    EntryPrice: decimal
    Symbol: Symbol
    IsolatedMargin: decimal
    Leverage: decimal
    LiquidationPrice: decimal
    MarginType: Types.FuturesMarginType
    PositionSide: PositionSide
    PositionAmount: decimal // Total size of the position (after including leverage)
    MarkPrice: decimal
    RealisedPnl: decimal option // we get this from the exchange (sometimes)
    UnrealisedPnl: decimal // we get this from the exchange
    CalculatedPnl: decimal option // we calculate this when there is a ticker update
    CalculatedPnlPercent: decimal option // calculated on ticker price update
    StoplossPnlPercentValue: decimal option // Stop loss expressed in terms of Pnl percent (not price)
    IsStoppedOut: bool
    CloseRaisedTime: DateTime option
    PositionInDb: PositionPnlView option // corresponding position in db, if found
}

// alias
type GetPositionsFromDataStore = ExchangeId -> Symbol -> PositionSide -> Async<Result<PositionPnlView option, exn>>

let private maybeSignalId (pos: PositionAnalysis) = 
    pos.PositionInDb |> Option.map (fun p -> p.SignalId) |> Option.defaultValue -1L

let private positions = new ConcurrentDictionary<PositionKey, PositionAnalysis>()

let private makePositionKey (position: PositionAnalysis) = 
    let (Symbol s) = position.Symbol
    let (ExchangeId e) = position.ExchangeId
    PositionKey <| sprintf "%d-%s-%A" e s position.PositionSide

let private makePositionKey' (position: ExchangePosition) =
    let (Symbol s) = position.Symbol
    let (ExchangeId e) = position.ExchangeId
    PositionKey <| sprintf "%d-%s-%A" e s position.Side

let private makePositionKey'' (ExchangeId e) (Symbol s) (positionSide: PositionSide) =
    PositionKey <| sprintf "%d-%s-%A" e s positionSide

let private closeSignal (exchangeName: string) (position: PositionAnalysis) (price: OrderBookTickerInfo) =
    async {
        let strategy = sprintf "position_analyser_close_%s" (string position.PositionSide) |> (fun s -> s.ToLowerInvariant())
        // check if we have already attempted a close recently
        if position.CloseRaisedTime.IsSome
        then
            // already raised a close - ignore
            return ()
        else
            Log.Information ("About to try to a close position: {Position} since we are stopped out", position)
            let (Symbol symbol) = position.Symbol
            let marketEvent = {
                MarketEvent.Name = strategy
                Price = price.BidPrice
                Symbol = symbol.ToUpperInvariant()
                Market = if (symbol.ToUpperInvariant()).EndsWith("PERP") then "USD" else "USDT" // hardcode for now
                TimeFrame = 1 // hardcode for now
                Exchange = exchangeName
                Category = "stopLoss"
                Contracts = 0M //Math.Abs(position.PositionAmount)  ignoring it for now as strategy handles the position information
            }

            let! result = raiseMarketEvent marketEvent
            match result with
            | Ok _ ->
                Log.Information("Raised a market event to close the long: {MarketEvent}", marketEvent)        
                let key = makePositionKey position
                positions.[key] <- {
                    position with CloseRaisedTime = Some DateTime.Now
                }
            | Result.Error s -> 
                Log.Error ("Error raising market event: {Error}", s)

    }

let private printPositionSummary () =
    positions.Values
    |> Seq.iter (fun pos -> 
            match pos.CalculatedPnl, pos.CalculatedPnlPercent with
            // find the units of qty: so we know what the pnl is in.
            // TODO fix this for COINM
            | Some pnl, Some pnlP ->
                let (Symbol s) = pos.Symbol
                let unitName = if s.EndsWith("USDT") then "USDT" else "???"
                Log.Information("Signal: {SignalId} - {Symbol} [{PositionSide}]: {Pnl:0.0000} {UnitName} [{PnlPercent:0.00} %]. Stop: {StopLossPercent:0.00} %", 
                    maybeSignalId pos,
                    pos.Symbol.ToString(), 
                    pos.PositionSide,
                    pnl, unitName, pnlP,
                    Option.defaultValue -9999M pos.StoplossPnlPercentValue) 
            | _ -> ()
        )
    Async.unit

let private getPositionsFromExchange (exchange: IFuturesExchange) (symbol: Symbol option) =
    async {
        let! positionsResult = exchange.GetFuturesPositions(symbol)

        let positions =
            match positionsResult with
            | Ok ps ->
                ps
                |> Seq.filter (fun p -> p.Amount <> 0m)
                |> Seq.map (fun p -> {
                        PositionAnalysis.EntryPrice = p.EntryPrice
                        Symbol = p.Symbol
                        Leverage = p.Leverage
                        IsolatedMargin = p.IsolatedMargin
                        LiquidationPrice = p.LiquidationPrice
                        MarginType = p.MarginType // cross or isolated
                        PositionSide = p.Side // long or short
                        PositionAmount = p.Amount
                        MarkPrice = p.MarkPrice
                        RealisedPnl = Some p.RealisedPnL
                        CalculatedPnl = None // calculated when we have a price ticker update
                        CalculatedPnlPercent = None
                        StoplossPnlPercentValue = None
                        UnrealisedPnl = p.UnRealisedPnL
                        IsStoppedOut = false
                        CloseRaisedTime = None
                        ExchangeId = exchange.Id
                        PositionInDb = None
                    })
            | Result.Error s ->
                Log.Error ("Error getting positions from {Exchange}: {Error}", exchange.Name, s)
                Seq.empty

        return positions
    }

let private savePositions (ps: (PositionAnalysis * PositionPnlView option) seq) = 
    ps 
    |> Seq.iter (fun (pos, posInDb) ->
            let key = makePositionKey pos
            let found, existingPosition = positions.TryGetValue key
            let updatedPosition = 
                if found then
                    {
                        pos with
                            CalculatedPnl = existingPosition.CalculatedPnl
                            CalculatedPnlPercent = existingPosition.CalculatedPnlPercent
                            IsStoppedOut = false // reset
                            StoplossPnlPercentValue = existingPosition.StoplossPnlPercentValue
                            PositionInDb =
                                if pos.PositionInDb.IsNone then posInDb else pos.PositionInDb
                    }
                else
                    pos
            positions.[key] <- updatedPosition
        )

let private removePositions (ps: PositionAnalysis seq) = 
    ps
    |> Seq.map makePositionKey
    |> Seq.iter (positions.Remove >> ignore)

let private removePositionsNotOnExchange (exchangeId: ExchangeId) (positionsOnExchange: PositionAnalysis seq) =
    let key p = makePositionKey p
    let lookup = 
        positionsOnExchange
        |> Seq.map (fun p -> (key p, p))
        |> Seq.groupBy fst
        |> Seq.map (fun (k, ps) -> (k, ps |> Seq.head |> snd))
        |> dict

    let notOnExchange (p: PositionAnalysis) =
        p.ExchangeId = exchangeId && not <| lookup.ContainsKey (key p)

    let positionsToRemove =
        positions.Values
        |> Seq.filter notOnExchange
        |> Seq.toList
    
    removePositions positionsToRemove

let private fetchPosition (exchange: IFuturesExchange) (p: ExchangePosition) =
    let key = makePositionKey' p
    let found, pos = positions.TryGetValue key
    async {
        let! pos' =
            // do we already know about this position in-memory?
            // if so - just update the position in memory with the latest 
            match found, p.Symbol with
            | true, _ ->
                    async {
                        return Some({
                            pos with
                                EntryPrice = p.EntryPrice
                                MarginType = p.MarginType
                                PositionAmount = p.Amount
                                PositionSide = p.Side
                                RealisedPnl = Some p.RealisedPnL 
                                UnrealisedPnl = p.UnRealisedPnL
                        })
                    }
            | false, s->
                // we don't know about this position in memory: fetch the full details from the Exchange
                getPositionsFromExchange exchange (Some s)
                |> Async.map Seq.tryHead // we expect to find only one position for a given Exchange, Symbol, PositionSide
        match pos' with
        | Some p -> positions.[key] <- p
        | None -> 
            Log.Warning ("Could not get position from {Exchange} for symbol: {Symbol}. Removing position assuming it is closed.", exchange.Name, p.Symbol)
            positions.Remove key |> ignore
     }

// input magic numbers for calculating stoploss from config: see story 127 // % - TODO move to config

let minStopLoss = -1M
let stopLossTriggerLevel = 1M
let stopLossFactor = 0.3M

let calculateStopLoss (position: PositionAnalysis) (currentGain: decimal option) =

    // inputs to calc
    let leverage = position.Leverage
    let prevGain = position.CalculatedPnlPercent
    let prevSL   = position.StoplossPnlPercentValue

    let withoutLeverageUsingDefault0 opt =
        opt
        |> Option.map (fun v -> v / leverage)
        |> Option.defaultValue 0M

    // calculated non-leveraged inputs
    let previousGainWithoutLeverage = prevGain |> withoutLeverageUsingDefault0  
    let gainWithoutLeverage = currentGain |> withoutLeverageUsingDefault0
    let previousSLWithoutLeverage = prevSL |> Option.map (fun v -> v / leverage)

    let toLeveragedValue (d: decimal option) =
        let round2 (d: decimal) = Math.Round (d, 2)   
        d |> Option.map (fun sl -> (sl * leverage) |> round2)

    let stopLossOfAtleast prevStopLoss newStopLoss = 
        // by this time we've already got a stop loss.
        // we only ever change stoploss upward
        List.max [
            newStopLoss
            prevStopLoss / 1M
            minStopLoss
        ]
        |> Some

    let calcSL v = 
        let newSL = gainWithoutLeverage - (stopLossFactor / gainWithoutLeverage)
        stopLossOfAtleast v newSL

    match previousSLWithoutLeverage with 
    | None when gainWithoutLeverage <> 0M &&
                gainWithoutLeverage > stopLossTriggerLevel ->
        calcSL minStopLoss

    | None ->
        Some minStopLoss // start with the minstoploss

    | Some v when gainWithoutLeverage <> 0M && 
                  gainWithoutLeverage > stopLossTriggerLevel && 
                  gainWithoutLeverage > previousGainWithoutLeverage ->
        calcSL v

    | v -> v // no change in stop loss

    |> toLeveragedValue

/// Calculates net PNL _after_ fees considering leverage.
let private calculatePnl (position: PositionAnalysis) (price: OrderBookTickerInfo) =
    let tradeFeesPercent = Strategies.Common.futuresTradeFeesPercentFor position.ExchangeId
    let qty = decimal <| Math.Abs(position.PositionAmount)
    let pnl = 
        match position.PositionSide with
        | LONG ->
            let grossPnl = qty * (price.AskPrice - position.EntryPrice) // this considers leverage, because qty is the full position size
            let fees = grossPnl * tradeFeesPercent * 2M // *2 because we need to consider fees for open and close
            grossPnl - fees |> Some
        | SHORT ->
            let grossPnl = qty * (position.EntryPrice - price.BidPrice) // this considers leverage, because qty is the full position size
            let fees = grossPnl * tradeFeesPercent * 2M // *2 because we need to consider fees for open and close
            grossPnl - fees |> Some
        | _ -> None
    let pnlPercent =
        pnl
        |> Option.map (fun pnl' ->
                if position.Leverage = 0M || position.PositionAmount = 0M || position.EntryPrice = 0M
                then None
                else 
                    let originalMargin = Math.Abs(position.PositionAmount) * position.EntryPrice / decimal position.Leverage
                    Some <| (100M * pnl' / originalMargin)
            )
        |> Option.flatten
    pnl, pnlPercent

let private isStopLossHit (position: PositionAnalysis) =
    match position.StoplossPnlPercentValue, position.CalculatedPnlPercent with
    | Some stoploss, Some gain when gain <= stoploss -> true 
    | _ -> false

let private printPositions (positions: PositionAnalysis seq) =
    positions
    |> Seq.filter (fun pos -> pos.PositionAmount <> 0m)
    |> Seq.iter (fun pos ->
            let signalId = maybeSignalId pos
            Log.Information("Signal: {SignalId} - Position: {Position}", signalId, pos) 
        )
    Log.Information("We have {PositionCount} open positions: {Positions}", positions |> Seq.length, positions |> Seq.toList)    

let private cleanUpStoppedPositions () =
    let positionsToRemove =
        positions.Values
        |> Seq.filter (fun p -> p.IsStoppedOut)
        |> Seq.toList
    
    removePositions positionsToRemove

let private refreshPositions (getPositionsFromDataStore: GetPositionsFromDataStore)
    (exchanges: IFuturesExchange seq) =

    exchanges
    |> Seq.map (fun exchange ->
        asyncResult {
            use _ = LogContext.PushProperty("Exchange", exchange.Name)
            Log.Information ("Refreshing positions from {Exchange}", exchange.Name)

            let! exchangePositions = getPositionsFromExchange exchange None
            Log.Information ("Found exisiting positions from exchange: {ExchangePositions}. Existing in-memory positions: {Positions}", exchangePositions, positions.Values |> Seq.toList)
            removePositionsNotOnExchange exchange.Id exchangePositions
            Log.Information ("In-memory positions after cleaning up: {Positions}", positions.Values |> Seq.toList)
            
            let! (positionsWithDbData: (PositionAnalysis * PositionPnlView option) array) = 
                exchangePositions
                |> Seq.map (fun p -> 
                        asyncResult {
                            let! positionInDb = getPositionsFromDataStore p.ExchangeId p.Symbol p.PositionSide
                            let poss =
                                match positionInDb with
                                | Some pos -> p, Some pos
                                | None -> p, None
                            return poss
                        }
                        |> AsyncResult.mapError (fun err ->
                                Log.Warning(err, "Error fetching position from db for exchange {Exchange}, symbol {Symbol}, side {PositionSide}: {Error}",
                                        exchange.Name,
                                        p.Symbol,
                                        p.PositionSide,
                                        err.Message
                                    )
                            )
                        |> Async.map (fun result -> Result.fold id (fun _ -> p, None) result)
                    )
                |> Async.Parallel
            
            savePositions positionsWithDbData |> ignore

            Log.Information "Now cleaning up old stopped out positions"
            cleanUpStoppedPositions ()

            printPositions positions.Values
        })
    |> Async.Parallel
    |> Async.Ignore

let private updatePositionPnl (getPositionsFromDataStore: GetPositionsFromDataStore) (exchange: IFuturesExchange) (price: OrderBookTickerInfo) =
    [ LONG; SHORT ]
    |> List.map ((makePositionKey'' exchange.Id price.Symbol) >>
                 (fun key ->  (positions.TryGetValue key), key))
    |> List.filter (fun ((found, pos), _) -> found && not pos.IsStoppedOut)
    |> List.iter (fun ((_, position), key) -> 
            let pnl, pnlPercent = calculatePnl position price
            let newStopLoss = calculateStopLoss position pnlPercent
            let pos' = 
                {
                    position with
                              CalculatedPnl = pnl
                              CalculatedPnlPercent = pnlPercent
                              StoplossPnlPercentValue = newStopLoss
                }
            
            let isStoppedOut = isStopLossHit pos'
            let pos'' = {
                pos' with
                     IsStoppedOut = pos'.IsStoppedOut || isStoppedOut 
            }
            positions.[key] <- pos''

            // raise signal / trade when stopped out
            if pos''.IsStoppedOut then
                async {
                    do! closeSignal exchange.Name positions.[key] price
                    do! Async.Sleep (45 * 1000) // 45 seconds
                    do! refreshPositions getPositionsFromDataStore [exchange]
                }
                |> Async.Start
        )

let private mkTradeAgent (getPositionsFromDataStore: GetPositionsFromDataStore) (exchanges: IFuturesExchange seq) =
    MailboxProcessor<PositionCommand>.Start (fun inbox ->
        let rec messageLoop() = async {
            let! msg = inbox.Receive()

            try
                match msg with
                | FuturesPositionUpdate (exchangeId, positionUpdates) ->
                    do!
                        match lookupExchange exchangeId with
                        | Result.Error s -> 
                            Log.Error ("FuturesPositionUpdate: Could not find exchange {ExchangeId}: {Error}" , exchangeId, s)
                            Async.singleton ()
                        | Ok exchange ->
                            // add or update positions from the incoming update (pushed from the Exchange via WS usually)
                            async {
                                do!
                                    positionUpdates
                                    |> Seq.map (fetchPosition exchange)
                                    |> Async.Parallel
                                    |> Async.Ignore

                                // NOT doing the reverse: remove any positions that are in-memory, but not in positionUpdates
                                // because this maybe a _partial_ position list pushed to us
                            }

                | FuturesBookPrice (exchangeId, bookPrice) ->
                    match lookupExchange exchangeId with
                    | Ok exchange ->
                        updatePositionPnl getPositionsFromDataStore exchange bookPrice
                    | Result.Error s -> 
                        Log.Error ("Could not find exchange {ExchangeId}: {Error}", exchangeId, s)

                | RefreshPositions ->
                    do! refreshPositions getPositionsFromDataStore exchanges

            with e ->
                Log.Error (e, "Error handling msg: {Error}", msg)

            return! messageLoop()
        }
        messageLoop()
    )


let trackPositions (getPositionsFromDataStore: GetPositionsFromDataStore) 
    (exchanges: IFuturesExchange seq) =

    use _x = LogContext.PushProperty ("Futures", true)

    Log.Information "Starting socket client for Binance futures user data stream"

    let tradeAgent = mkTradeAgent getPositionsFromDataStore exchanges
    
    // need to add an error handler to ensure the process crashes properly
    // we might later need to make this smarter, to crash only on repeated exceptions of the same kind or 
    // something where the exception is unrecoverable
    tradeAgent.Error.Add(raise)

    repeatEvery (TimeSpan.FromSeconds(3.0)) printPositionSummary "PositionSummaryPrinter" |> Async.Start

    // required to ensure we get reasonably fresh data about positions
    repeatEvery (TimeSpan.FromSeconds(15.0)) (fun () -> refreshPositions getPositionsFromDataStore exchanges) "PositionRefresh" |> Async.Start

    exchanges
    |> Seq.map (fun exchange -> exchange.TrackPositions tradeAgent)
    |> Async.Parallel
    |> Async.Ignore