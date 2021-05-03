module Binance.Spot.TradeStatusUpdater

open Serilog
open FSharp.Control
open FSharpx.Control
open DbTypes
open System
open Types
open Binance.ApiTypes
open Serilog.Context

let rnd = System.Random()

(*
At start of the app:
- get all trade with a given beyond a known id
- For Binance: we need to do this by symbol
    - For each order, we need to query trades and then find related orders 
*)
let private getOrderAndTradeDetails { ExchangeSymbolAndTradeId.Symbol = symbol; TradeId = tradeId } =
    let delayQueryOrder (orderId: int64) =
        async {
            let sleepSeconds = rnd.Next(1000, 2000)
            do! Async.Sleep(sleepSeconds)
            Log.Debug("Getting order and trade details for orderId {OrderId}", orderId)
            let id = { OrderQueryInfo.OrderId = OrderId <| string orderId; Symbol = Symbol symbol }
            return! (Trade.queryOrder id)
        }
    
    let findTradesForOrder (allTrades: Result<BinanceMyTradesResponse [], string>) (order: Result<BinanceOrderQueryResponse, string>) = 
        match order, allTrades with
        | Ok o, Ok ts -> Ok (o, ts |> Array.filter (fun t -> t.OrderId = o.OrderId))
        | _, Result.Error s -> Result.Error s
        | Result.Error s, _ -> Result.Error s

    async {
        let sleepSeconds = rnd.Next(1500, 3500)
        do! Async.Sleep(sleepSeconds) 
        Log.Debug ("Getting trades for symbol {Symbol}, from trade id {FromTradeId}", symbol, tradeId)
        let! trades = Trade.queryTrades (Symbol symbol) (Some <| tradeId + 1L)
        
        let orderIds = trades |> Result.map (fun ts -> ts |> Array.map (fun t -> t.OrderId) |> Array.distinct)
        match orderIds with
        | Ok oids ->
            Log.Debug ("Found {OrderCountForTrades} orders to query for symbol {Symbol}",
                    oids.Length, symbol
                )
            let! orders =
                oids
                |> Array.map (delayQueryOrder >> (Async.map (findTradesForOrder trades)))
                |> Async.ParallelWithThrottle 1
                |> Async.map (Array.toList >> (List.fold ResultEx.combineList (Ok [])))
            return orders
        | Result.Error s ->
            Log.Warning ("Error getting order and trade details for {Symbol}, from {FromTradeId}", symbol, tradeId)
            return Result.Error s
    }

let private upsertOrder 
                (getExchangeOrder: int64 -> Async<option<ExchangeOrder>>) 
                saveOrder 
                ((order, trades): BinanceOrderQueryResponse * BinanceMyTradesResponse[]) = 
    async {
        let toUpper (s: string) = s.ToUpper()
        let parsedSignalId, parsedInternalOrderId = TradeStatusListener.parseOrderIds order.ClientOrderId

        match parsedSignalId, parsedInternalOrderId with
        | Some signalId, Some internalOrderId ->
            use _ = LogContext.PushProperty("InternalOrderId", internalOrderId)
            use _ = LogContext.PushProperty("SignalId", signalId)

            Log.Debug("Upserting {OrderSide} order for {OrderId} {Symbol}. (Internal order id: {InternalOrderId})",
                    order.Side, order.OrderId, order.Symbol, internalOrderId
                )

            let! previousOrder = getExchangeOrder internalOrderId

            if previousOrder.IsNone
            then Log.Debug("Did not find previous order in db: {OrderId}", order.OrderId)
            else Log.Debug("Found previous order in db: {OrderId}", order.OrderId)

            let createdOrUpdatedTime = order.Time |> DateTimeOffset.FromUnixTimeMilliseconds |> (fun d -> d.UtcDateTime)
            // this may be a new order placed directly on the exchange
            let exo = {
                ExchangeOrder.Id = previousOrder|> Option.map (fun o -> o.Id) |> Option.defaultValue 0L 
                ExchangeId = Binance.ExchangeId
                OrderSide = order.Side |> toUpper
                ExchangeOrderId = string order.OrderId
                ExchangeOrderIdSecondary = order.ClientOrderId
                SignalId = signalId
                Status = order.Status |> toUpper
                StatusReason = "Full query update"
                Symbol = order.Symbol
                Price = order.Price
                ExecutedPrice = order.Price
                OriginalQty = order.OrigQty
                ExecutedQty = trades |> Seq.sumBy (fun t -> t.Qty)
                FeeAmount = trades |> Seq.sumBy (fun t -> t.Commission)
                FeeCurrency = trades |> Seq.head |> (fun t -> t.CommissionAsset)
                LastTradeId = trades |> Seq.map (fun t -> t.Id) |> Seq.max
                CreatedTime = previousOrder |> Option.map (fun o -> o.CreatedTime) |> Option.defaultValue createdOrUpdatedTime
                UpdatedTime = previousOrder |> Option.map (fun o -> o.UpdatedTime) |> Option.defaultValue createdOrUpdatedTime
            }

            Log.Debug ("Saving order {OrderId}", order.OrderId)
            do! saveOrder exo SPOT |> Async.Ignore
            return Some exo
        | _ ->
            Log.Warning("TradeStatusUpdater - could not parse signal id from clientOrderId: {ClientOrderId}. Exchange's order: {@Order}", order.ClientOrderId, order)
            return None
    }

let private upsertOrders getExchangeOrder saveOrder (ordersForSymbol: Async<Result<(BinanceOrderQueryResponse * BinanceMyTradesResponse[]) list, string>>) =
    async {
        match! ordersForSymbol with
        | Result.Error s -> 
            Log.Warning ("Error retrieving orders and trades: {ErrorMessage}", s) 
        | Ok orders ->
            do! orders
                |> Seq.map (upsertOrder getExchangeOrder saveOrder)
                |> Async.ParallelIgnore 1
    }

let updateOrderStatuses getTradedSymbols getExchangeOrder saveOrder =
    async {
        Log.Information ("Starting update order statuses")
        let! symbols = getTradedSymbols Binance.ExchangeId
        Log.Debug ("Found {DistinctTradedSymbolCount} symbols traded on Binance. Querying orders...", symbols |> Seq.length)
        do!
            symbols
            |> Seq.map getOrderAndTradeDetails
            |> AsyncSeq.ofSeq
            |> AsyncSeq.iterAsync (upsertOrders getExchangeOrder saveOrder)
    }
