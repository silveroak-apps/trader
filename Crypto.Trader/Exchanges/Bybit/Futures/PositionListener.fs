module Bybit.Futures.PositionListener
open Types
open System

open FSharp.Linq
open FsToolkit.ErrorHandling

open Bybit.Futures.Common
open Serilog.Context
open Serilog
open System.Collections.Concurrent

type BybitLinearPositionListResultBase = IO.Swagger.Model.LinearPositionList
type BybitLinearPosition = IO.Swagger.Model.LinearPositionListResult

type BybitPositionListResultBase = IO.Swagger.Model.Position
type BybitPosition = IO.Swagger.Model.PositionInfo

type private BybitPositionKey = {
    Side: PositionSide
    Type: Types.FuturesMarginMode
    Symbol: Symbol
}

let private toKey (p: ExchangePosition) =
    {
        BybitPositionKey.Side = p.Side
        Symbol = p.Symbol
        Type = if p.Symbol.ToString().EndsWith("USDT") then USDT else COINM
    }

let private positions = new ConcurrentDictionary<BybitPositionKey, ExchangePosition>()

let toExchangePosition (p: BybitLinearPosition) : Types.ExchangePosition =
    {
        Types.ExchangePosition.MarginType = FuturesMarginType.UNKNOWN // for now the lib doesn't expose this. need to fix
        Leverage = p.Leverage.GetValueOrDefault () |> decimal
        Side = 
            match p.Side with 
            | "Buy"     -> PositionSide.LONG
            | "Sell"    -> PositionSide.SHORT
            | _         -> PositionSide.NOT_APPLICABLE
        Symbol = Symbol p.Symbol
        EntryPrice = p.EntryPrice.GetValueOrDefault () |> decimal
        MarkPrice = 0M // doesn't look like the api returns this
        Amount = p.Size.GetValueOrDefault () |> decimal
        RealisedPnL = p.CumRealisedPnl.GetValueOrDefault() |> decimal        
        UnRealisedPnL = 0M // looks like this isn't returned in the response
        IsolatedMargin = p.PositionMargin.GetValueOrDefault() |> decimal // TODO find out if this is right?
        LiquidationPrice = p.LiqPrice.GetValueOrDefault() |> decimal
    }

let private toExchangePosition' (p: BybitPosition) : Types.ExchangePosition =
    {
        Types.ExchangePosition.MarginType = FuturesMarginType.UNKNOWN // for now the lib doesn't expose this. need to fix
        Leverage = p.Leverage.GetValueOrDefault () |> decimal
        Side = 
            match p.Side with 
            | "Buy"     -> PositionSide.LONG
            | "Sell"    -> PositionSide.SHORT
            | _         -> PositionSide.NOT_APPLICABLE
        Symbol = Symbol p.Symbol
        EntryPrice = p.EntryPrice.GetValueOrDefault () |> decimal
        MarkPrice = 0M // doesn't look like the api returns this
        Amount = p.Size.GetValueOrDefault () |> decimal
        RealisedPnL = p.CumRealisedPnl.GetValueOrDefault() |> decimal
        UnRealisedPnL = p.UnrealisedPnl.GetValueOrDefault() |> decimal
        IsolatedMargin = p.PositionMargin.GetValueOrDefault() |> decimal // TODO find out if this is right?
        LiquidationPrice = p.LiqPrice.GetValueOrDefault() |> decimal
    }

let private getUSDTPositionsFromBybitAPI (client: BybitUSDTPositionsApi) (symbolFilter: Symbol option) =
    async {
        
        let! response =
            match symbolFilter with
            | Some (Symbol s) -> client.GetActivePositionsAsync s
            | None            -> client.GetActivePositionsAsync ()
            |> Async.AwaitTask
        

        if response.RetCode ?= 0M
        then
            return Ok (response.GetResult() |> Seq.map toExchangePosition)
        else
            return Result.Error (sprintf "Error getting USDT positions from Bybit: [%A]%s" response.RetCode response.RetMsg)
    }

let private getCoinMPositionsFromBybitAPI (client: BybitCoinMPositionsApi) (symbolFilter: Symbol option) =
    async {
        
        let! response =
            match symbolFilter with
            | Some (Symbol s) -> client.GetActivePositionsAsync s
            | None            -> client.GetActivePositionsAsync ()
            |> Async.AwaitTask

        if response.RetCode ?= 0M
        then
            let result = 
                response.GetResult()
                |> Seq.map toExchangePosition'
                |> Ok
            return result
        else
            return Result.Error (sprintf "Error getting COINM positions from Bybit: [%A]%s" response.RetCode response.RetMsg)
    }

let getPositions (symbolFilter: Symbol option): Async<Result<seq<ExchangePosition>, string>> =
    async {
        let usdtClient = BybitUSDTPositionsApi(config)
        let coinMClient = BybitCoinMPositionsApi(config)
        let! result =
            match symbolFilter with
            | Some (Symbol s) ->
                if s.EndsWith "USDT"
                then getUSDTPositionsFromBybitAPI usdtClient symbolFilter
                else getCoinMPositionsFromBybitAPI coinMClient symbolFilter
            | None ->
                asyncResult {
                    let! usdtPositions = getUSDTPositionsFromBybitAPI usdtClient None
                    let! coinmPositions = getCoinMPositionsFromBybitAPI coinMClient None
                    return usdtPositions |> Seq.append coinmPositions
                }

        return result
    }

let private getLatestPrices (symbols: Symbol seq) =    
    symbols
    |> Seq.map (fun (Symbol s) -> 
        async {
            try
                let! ob = Common.getOrderBookCurrentPrice (Symbol s)
                return ob
            with ex ->
                return Error <| sprintf "Error getting latest prices from orderbook for symbol %s from %s: %s" s Common.ExchangeName (ex.ToString())
        })   
    |> Async.Parallel

let trackPositions (agent: MailboxProcessor<PositionCommand>) =

    // Ideally, we should subscribe to websocket(s), but for now:
    // just do some polling

    // the higher level FuturesPositionAnalyser will poll for positions anyway.
    // we just need to poll for prices every 2 seconds to update position data

    let fetchPriceUpdatesAndNotify () =
        asyncResult {

            let symbols = 
                [Common.coinMSymbols.Keys; Common.usdtSymbols.Keys]
                |> Seq.concat 

            let! prices = getLatestPrices symbols
            prices
            |> Seq.iter (fun r ->
                match r with
                | Ok orderBookTicker -> 
                    agent.Post (FuturesBookPrice (Types.ExchangeId Common.ExchangeId, orderBookTicker))
                | Error s -> Log.Warning ("Error getting orderbook prices from Bybit. Will try again later. Error: {Error}", s)
            )
        }
        |> Async.Ignore
        
    let _2Seconds = TimeSpan.FromSeconds 2.0
    repeatEvery _2Seconds fetchPriceUpdatesAndNotify "BybitPricePoller"

    