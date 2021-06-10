module Bybit.Futures.PositionListener
open Types
open System

open FSharp.Linq
open FsToolkit.ErrorHandling

open Bybit.Futures.Common
open Serilog.Context
open Serilog
open System.Collections.Concurrent

type BybitLinearPositionListResultBase = IO.Swagger.Model.LinearPositionListResultBase
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
        Side = PositionSide.FromString p.Side
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
        Side = PositionSide.FromString p.Side
        Symbol = Symbol p.Symbol
        EntryPrice = p.EntryPrice.GetValueOrDefault () |> decimal
        MarkPrice = 0M // doesn't look like the api returns this
        Amount = p.Size.GetValueOrDefault () |> decimal
        RealisedPnL = p.CumRealisedPnl.GetValueOrDefault() |> decimal
        UnRealisedPnL = p.UnrealisedPnl.GetValueOrDefault() |> decimal
        IsolatedMargin = p.PositionMargin.GetValueOrDefault() |> decimal // TODO find out if this is right?
        LiquidationPrice = p.LiqPrice.GetValueOrDefault() |> decimal
    }

let getUSDTPositionsFromBybitAPI (client: BybitUSDTPositionsApi) (symbolFilter: Symbol option) =
    async {
        
        let! responseObj =
            match symbolFilter with
            | Some (Symbol s) -> client.LinearPositionsMyPositionAsync s
            | None            -> client.LinearPositionsMyPositionAsync ()
            |> Async.AwaitTask

        let jobj = responseObj :?> Newtonsoft.Json.Linq.JObject
        let response = jobj.ToObject<BybitLinearPositionListResultBase>()
        if response.RetCode ?= 0M
        then
            return Ok (response.Result |> Seq.map toExchangePosition)
        else
            return Result.Error (sprintf "Error getting USDT positions from Bybit: [%A]%s" response.RetCode response.RetMsg)
    }

let getCoinMPositionsFromBybitAPI (client: BybitCoinMPositionsApi) (symbolFilter: Symbol option) =
    async {
        
        let! responseObj =
            match symbolFilter with
            | Some (Symbol s) -> client.PositionsMyPositionAsync s
            | None            -> client.PositionsMyPositionAsync ()
            |> Async.AwaitTask

        let jobj = responseObj :?> Newtonsoft.Json.Linq.JObject
        let response = jobj.ToObject<BybitPositionListResultBase>()
        if response.RetCode ?= 0M
        then
            return Ok (
                response.Result :?> Newtonsoft.Json.Linq.JObject
                |> (fun j -> j.ToObject<BybitPosition seq>()) 
                |> Seq.map toExchangePosition')
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
    let client = IO.Swagger.Api.MarketApi()
    
    let getPrice (Symbol s) =
        async {
            let! o = client.MarketOrderbookAsync(s) |> Async.AwaitTask
            let jobj = o :?> Newtonsoft.Json.Linq.JObject
            let obResponse = jobj.ToObject<IO.Swagger.Model.OrderBookBase>()
            let result =
                if obResponse.RetCode ?= 0M && 
                    obResponse.Result.Count > 0
                then
                    
                    let buys = obResponse.Result |> Seq.filter(fun obItem -> obItem.Side = "Buy")
                    let sells = obResponse.Result |> Seq.filter(fun obItem -> obItem.Side = "Sell")

                    match Seq.length buys, Seq.length sells with
                    | 0, _ -> Result.Error (sprintf "No buy orders in Bybit orderbook for %s" s)
                    | _, 0 -> Result.Error (sprintf "No sell orders in Bybit orderbook for %s" s)
                    | _, _ -> 
                        let bestBuy = buys |> Seq.maxBy (fun buy -> buy.Price)
                        let bestSell = sells |> Seq.minBy (fun sell -> sell.Price)

                        Result.Ok {
                            OrderBookTickerInfo.AskPrice = Decimal.Parse bestSell.Price
                            AskQty = bestSell.Size.GetValueOrDefault()
                            BidPrice = Decimal.Parse bestBuy.Price
                            BidQty = bestBuy.Size.GetValueOrDefault()
                            Symbol = Symbol s
                        }

                elif obResponse.RetCode ?= 0M
                then Result.Error (sprintf "Error getting prices from ByBit: '%s'" obResponse.RetMsg)
                else Result.Error (sprintf "No results for Bybit orderbook API call")
            return result
        }

    symbols
    |> Seq.map getPrice
    |> Async.Parallel

let trackPositions (agent: MailboxProcessor<PositionCommand>) (symbols: Symbol seq) =

    // Ideally, we should subscribe to websocket(s), but for now:
    // just do some polling

    // the higher level FuturesPositionAnalyser will poll for positions anyway.
    // we just need to poll for prices every 2 seconds to update position data

    let fetchPriceUpdatesAndNotify () =
        asyncResult {
            let! prices = getLatestPrices symbols
            prices
            |> Seq.iter (fun pr ->
                match pr with
                | Error s -> Log.Error("Error getting price from Bybit futures: {ErrorMsg}", s)
                | Ok orderBookTicker -> 
                    agent.Post (FuturesBookPrice (Types.ExchangeId Common.ExchangeId, orderBookTicker))
            )
        }
        |> Async.Ignore
        
    let _2Seconds = TimeSpan.FromSeconds 2.0
    repeatEvery _2Seconds fetchPriceUpdatesAndNotify "BybitPricePoller"

    