﻿module Strategies.FuturesPositionAnalyser

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

type PositionKey = PositionKey of string

// TODO move this to db config table
let private tradeFeesPercent = 0.04m // assume the worst: current market order fees for Binance is 0.04% (https://www.binance.com/en/support/articles/360033544231)
type private PositionAnalysis = {
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
}

let private positions = new ConcurrentDictionary<PositionKey, PositionAnalysis>()

let private makePositionKey (Symbol symbol) (positionSide: PositionSide) = PositionKey <| sprintf "%s-%s" symbol (positionSide.ToString())

let private closeSignal (exchangeName: string) (position: PositionAnalysis) (price: OrderBookTickerInfo) =
    async {
        let strategy = "position_analyser_close"
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
                TimeFrame = "1" // hardcode for now
                Exchange = exchangeName
                Category = "StopLoss"
                Contracts = Math.Abs(position.PositionAmount)
            }

            let! result = raiseMarketEvent marketEvent
            match result with
            | Ok _ ->
                Log.Information("Raised a market event to close the long: {MarketEvent}", marketEvent)        
                let key = makePositionKey position.Symbol position.PositionSide
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
            | Some pnl, Some pnlP ->
                let symbol = pos.Symbol.ToString()
                let found, symbolUnits = coinMSymbols.TryGetValue symbol
                let pnlUsd, unitName = 
                    if found then (pnl / decimal symbolUnits.Multiplier), "USD"
                    else pnl, "USDT"

                Log.Information("{Symbol} [{PositionSide}]: {Pnl:0.0000} {UnitName} [{PnlPercent:0.00} %]. Stop: {StopLossPercent:0.00} %", 
                    pos.Symbol.ToString(), 
                    pos.PositionSide,
                    pnlUsd, unitName, pnlP,
                    Option.defaultValue -9999M pos.StoplossPnlPercentValue) 
            | _ -> ()
        )
    Async.unit

let private getPositionsFromExchange (exchange: IFuturesExchange) (symbol: string option) =
    async {
        let! positionsResult =
            match symbol with
            | Some s -> exchange.GetFuturesPositions(s.Replace("_PERP", "")) // TODO move to Binance specific impl
            | None   -> exchange.GetFuturesPositions("")

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
                        // EntryTime we don't actually know - unless we query orders and guess / calculate over time :|
                    })
            | Result.Error s ->
                Log.Error ("Error getting positions from {Exchange}: {Error}", exchange.Name, s)
                Seq.empty

        return positions
    }

let private savePositions (ps: PositionAnalysis seq) = 
    ps 
    |> Seq.iter (fun pos ->
            let key = makePositionKey pos.Symbol pos.PositionSide
            let found, existingPosition = positions.TryGetValue key
            let updatedPosition = 
                if found then
                    {
                        pos with
                            CalculatedPnl = existingPosition.CalculatedPnl
                            CalculatedPnlPercent = existingPosition.CalculatedPnlPercent
                            IsStoppedOut = false // reset
                            StoplossPnlPercentValue = existingPosition.StoplossPnlPercentValue
                    }
                else
                    pos
            positions.[key] <- updatedPosition
        )

let private fetchPosition (exchange: IFuturesExchange) (p: ExchangePosition) =
    let key = makePositionKey p.Symbol p.Side
    let found, pos = positions.TryGetValue key
    async {
        let! pos' =
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
            | false, (Symbol s) ->
                getPositionsFromExchange exchange (Some s)
                |> Async.map Seq.tryHead
        match pos' with
        | Some p -> positions.[key] <- p
        | None -> 
            Log.Warning ("Could not get position from {Exchange} for symbol: {Symbol}. Removing position assuming it is closed.", exchange.Name, p.Symbol)
            positions.Remove key |> ignore
     }

let private calculateStopLoss (position: PositionAnalysis) (gainOpt: decimal option) =
    let minStopLoss = -1M * decimal position.Leverage // % - TODO move to config
    let previousStopLossValue = position.StoplossPnlPercentValue
    let gain = Option.defaultValue 0M gainOpt

    let trailingTakeProfitLevel = 1M * decimal position.Leverage // % - TODO move to config
    let trailingDistance = 0.5M * decimal position.Leverage // % - TODO move to config

    let breakEvenLevel = 0.4M * decimal position.Leverage // % - TODO move to config

    let stopLossOfAtleast prevStopLoss newStopLoss = 
        // by this time we've already got a stop loss.
        // we only ever change stoploss upward
        List.max [
            newStopLoss
            prevStopLoss / 1M
            minStopLoss
        ]
        |> Some

    match previousStopLossValue with
    | None ->
        Some minStopLoss // always start with the minstoploss    

    | Some v when gain >= trailingTakeProfitLevel ->
        let newStopLoss = gain - trailingDistance
        let sl = stopLossOfAtleast v newStopLoss
        sl

    | Some v when gain >= breakEvenLevel ->
        let newStopLoss = breakEvenLevel / 2M
        let sl = stopLossOfAtleast v newStopLoss
        sl

    | v -> v // no change in stop loss

let private calculatePnl (position: PositionAnalysis) (price: OrderBookTickerInfo) =
    let qty = decimal <| Math.Abs(position.PositionAmount)
    let pnl = 
        match position.PositionSide with
        | LONG ->
            let grossPnl = qty * (price.AskPrice - position.EntryPrice)
            let fees = grossPnl * tradeFeesPercent
            grossPnl - fees |> Some
        | SHORT ->
            let grossPnl = qty * (position.EntryPrice - price.BidPrice)
            let fees = grossPnl * tradeFeesPercent
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
            Log.Information("Position: {@Position}", pos) 
        )

let private cleanUpStoppedPositions () =
    let positionsToRemove =
        positions.Values
        |> Seq.filter (fun p -> p.IsStoppedOut)
        |> Seq.toList
    
    positionsToRemove
    |> Seq.map (fun p -> makePositionKey p.Symbol p.PositionSide)
    |> Seq.iter (fun key -> positions.Remove key |> ignore)

let private refreshPositions (exchange: IFuturesExchange seq) =
    exchange
    |> Seq.map (fun exchange ->
        async {
            Log.Information ("Refreshing positions from {Exchange}", exchange.Name)
            
            let! positions = getPositionsFromExchange exchange None
            savePositions positions

            Log.Information "Now cleaning up old stopped out positions"
            cleanUpStoppedPositions ()

            // Log.Information ("We have {PositionCount} positions now.", positions.Count)
            printPositions positions
        })
    |> Async.Parallel
    |> Async.Ignore

let private updatePositionPnl (exchange: IFuturesExchange) (price: OrderBookTickerInfo) =
    [ LONG; SHORT ]
    |> List.map (makePositionKey price.Symbol)
    |> List.map (fun key ->  (positions.TryGetValue key), key)
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
                    do! refreshPositions [exchange]
                }
                |> Async.Catch
                |> Async.map (fun c ->
                        match c with
                        | Choice2Of2 ex -> 
                            Log.Warning(ex, "Error trying to close position: {Error}", ex.Message)
                            c
                        | _ -> c
                    )
                |> Async.Ignore
                |> Async.Start
        )

let private mkTradeAgent (exchanges: IFuturesExchange seq) =
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
                            // add or update positions from the incoming update
                            async {
                                do!
                                    positionUpdates
                                    |> Seq.map (fetchPosition exchange)
                                    |> Async.Parallel
                                    |> Async.Ignore

                                // do the reverse: remove any positions that are in-memory, but not in accountUpdate
                                positions.Values
                                |> Seq.map (fun p -> makePositionKey p.Symbol p.PositionSide)
                                |> Seq.except (
                                        positionUpdates
                                        |> Seq.map (fun p -> makePositionKey p.Symbol p.Side)
                                    )
                                |> Seq.iter (fun pos -> positions.Remove pos |> ignore)

                                return ()
                            }

                | FuturesBookPrice (exchangeId, bookPrice) ->
                    match lookupExchange exchangeId with
                    | Ok exchange ->
                        updatePositionPnl exchange bookPrice
                    | Result.Error s -> 
                        Log.Error ("Could not find exchange {ExchangeId}: {Error}", exchangeId, s)

                | RefreshPositions ->
                    do! refreshPositions exchanges

            with e ->
                Log.Error (e, "Error handling msg: {Error}", msg)

            return! messageLoop()
        }
        messageLoop()
    )

let trackPositions (exchanges: IFuturesExchange seq) (symbols: string seq) =
    use _x = LogContext.PushProperty ("Futures", true)

    Log.Information "Starting socket client for Binance futures user data stream"

    let tradeAgent = mkTradeAgent exchanges
    
    // need to add an error handler to ensure the process crashes properly
    // we might later need to make this smarter, to crash only on repeated exceptions of the same kind or 
    // something where the exception is unrecoverable
    tradeAgent.Error.Add(raise)

    tradeAgent.Post RefreshPositions

    repeatEvery (TimeSpan.FromSeconds(3.0)) printPositionSummary "PositionSummaryPrinter" |> Async.Start
    repeatEvery (TimeSpan.FromSeconds(15.0)) (fun () -> refreshPositions exchanges) "PositionRefresh" |> Async.Start

    exchanges
    |> Seq.map (fun exchange -> exchange.TrackPositions (tradeAgent, symbols))
    |> Async.Parallel
    |> Async.Ignore
