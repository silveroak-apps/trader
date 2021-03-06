module Binance.Futures.Trade

open Binance.Net
open Binance.Net.Objects.Futures.FuturesData

open System
open Types
open Binance.Net.Interfaces.SubClients.Futures
open Serilog

open Exchanges.Common
open Binance.Futures.Common

let private getClient futuresMode =
    let c = getBaseClient ()
    match futuresMode with
    | USDT -> c.FuturesUsdt.Order :> IBinanceClientFuturesOrders
    | COINM  -> c.FuturesCoin.Order :> IBinanceClientFuturesOrders

// A bit ugly: the library we're using seems to not unify the types for PlacedOrder vs Order :/
// So we do it ourselves
let mapOrderStatus 
    (order: {| 
                Status: Binance.Net.Enums.OrderStatus
                ExecutedQuantity: decimal
                AvgPrice: decimal
            |}
    ) =
    match order.Status with
    | Enums.OrderStatus.New             -> OrderNew
    | Enums.OrderStatus.Canceled        -> OrderCancelled (Qty order.ExecutedQuantity, Price order.AvgPrice)
    | Enums.OrderStatus.Expired         -> OrderCancelled (Qty 0M, Price 0M)
    | Enums.OrderStatus.Filled          -> OrderFilled (Qty order.ExecutedQuantity, Price order.AvgPrice)
    | Enums.OrderStatus.PartiallyFilled -> OrderPartiallyFilled(Qty order.ExecutedQuantity, Price order.AvgPrice)
    | Enums.OrderStatus.Rejected        -> OrderCancelled (Qty 0M, Price 0M)
    | status                            -> OrderQueryFailed (sprintf "Unrecognised order status: %A" status)

let private toOrderInfoResult (originalPrice: decimal) (orderData: BinanceFuturesPlacedOrder) =
    {
        OrderInfo.OrderId = OrderId (string orderData.OrderId)
        ClientOrderId = ClientOrderId orderData.ClientOrderId
        ExecutedQuantity = Qty orderData.ExecutedQuantity
        Time = orderData.UpdateTime |> DateTimeOffset
        Quantity = Qty orderData.OriginalQuantity
        Price = Price originalPrice
        Symbol = Symbol orderData.Symbol
        Status = 
            mapOrderStatus {| 
                                Status = orderData.Status
                                ExecutedQuantity = orderData.ExecutedQuantity
                                AvgPrice = originalPrice |}
    } |> Ok

let private orderTypeFrom (ot: OrderType) = 
    match ot with
    | LIMIT -> Enums.OrderType.Limit
    | _ -> Enums.OrderType.Market

let private placeOrder (o: OrderInputInfo) : Async<Result<OrderInfo, OrderError>> =
    let (Symbol s) = o.Symbol
    let client = getClient (getFuturesMode o.Symbol)

    let orderSide = 
        match o.OrderSide with
        | BUY  -> Enums.OrderSide.Buy
        | SELL -> Enums.OrderSide.Sell
        | _ -> raise <| exn "Unknown order side not supported"

    let positionSide =
        match o.PositionSide with
        | LONG  -> Enums.PositionSide.Long
        | SHORT -> Enums.PositionSide.Short
        | _ -> raise <| exn "Not_Applicable position side not suported"

    async {
        let orderType = orderTypeFrom o.OrderType

        // looks like timeInForce is not needed for Market orders. (in fact, it fails if sent)
        let price = if o.OrderType = MARKET then Nullable() else Nullable (o.Price / 1M<price>)
        let timeInForce = if o.OrderType = MARKET then Nullable() else Nullable Enums.TimeInForce.GoodTillCancel

        let! orderResponse = 
            client.PlaceOrderAsync(
                    newClientOrderId = string o.SignalId,
                    symbol           = s,
                    side             = orderSide,
                    ``type``         = orderType,
                    price            = price,
                    quantity         = o.Quantity / 1M<qty>,
                    positionSide     = positionSide,
                    timeInForce      = timeInForce
                )
            |> Async.AwaitTask
        let result = 
            if orderResponse.Success
            then toOrderInfoResult (o.Price / 1M<price>) orderResponse.Data
            else 
                Error(OrderError(sprintf "%A: %s" orderResponse.Error.Code orderResponse.Error.Message))
        return result
    }

let private queryOrderStatus (o: OrderQueryInfo) =
    let (Symbol symbol) = o.Symbol
    let client = getClient (getFuturesMode o.Symbol)
    let (OrderId sOrderId) = o.OrderId

    let parsed, orderId = Int64.TryParse sOrderId
    if not parsed then raise <| exn (sprintf "Invalid orderId. Expecting an integer. Found: %s" sOrderId)
    async {
        let! orderResponse = client.GetOrderAsync (symbol, orderId) |> Async.AwaitTask
        if orderResponse.Success
        then 
            let order = orderResponse.Data
            return mapOrderStatus {| 
                                    Status = order.Status
                                    ExecutedQuantity = order.ExecutedQuantity
                                    AvgPrice = order.AvgPrice |}
        else
            return OrderQueryFailed (sprintf "%A: %s" orderResponse.Error.Code orderResponse.Error.Message)
    }

let private cancelOrder (o: OrderQueryInfo) =
    let (Symbol symbol) = o.Symbol
    let client = getClient (getFuturesMode o.Symbol)
    let (OrderId sOrderId) = o.OrderId

    let parsed, orderId = Int64.TryParse sOrderId
    if not parsed then raise <| exn (sprintf "Invalid orderId. Expecting an integer. Found: %s" sOrderId)
    async {
        let! cancelResponse = client.CancelOrderAsync (symbol, orderId) |> Async.AwaitTask
        return 
            if cancelResponse.Success
            then Ok true
            elif cancelResponse.Error.Code.GetValueOrDefault() = -2011 // Unknown order sent (cancel was rejected: happens when order was already filled)
            then Ok false
            else Error (sprintf "%A: %s" cancelResponse.Error.Code cancelResponse.Error.Message)
    }

let private getOrderBookCurrentPrice (Symbol s) =
    let client = getBaseClient ()
    // ugly copy paste of a large section of code - because the library we use doesn't unify the types that pull OrderBook for
    // COIN-M vs USDT futures
    // and I didn't bother to write an abstraction over it.
    async {
        let futuresMode = getFuturesMode (Symbol s)
        match futuresMode with
        | USDT ->
            let! bookResponse = client.FuturesUsdt.Market.GetBookPricesAsync(s) |> Async.AwaitTask
            if not bookResponse.Success
            then 
                return Result.Error (sprintf "Error getting orderbook: %A: %s" bookResponse.Error.Code bookResponse.Error.Message)
            else
                return
                    bookResponse.Data
                    |> Seq.tryHead
                    |> Option.map (fun o -> 
                                    {
                                        OrderBookTickerInfo.AskPrice = o.BestAskPrice
                                        AskQty   = o.BestAskQuantity
                                        BidPrice = o.BestBidPrice
                                        BidQty   = o.BestBidQuantity
                                        Symbol   = Symbol s
                                    })
                    |> (fun ob ->
                            match ob with
                            | Some v -> Result.Ok v
                            | None -> Result.Error "No orderbook data found"
                        ) 

        | COINM ->
            let! bookResponse = client.FuturesCoin.Market.GetBookPricesAsync(s) |> Async.AwaitTask
            if not bookResponse.Success
            then 
                return Result.Error (sprintf "Error getting orderbook: %A: %s" bookResponse.Error.Code bookResponse.Error.Message)
            else 
                return
                    bookResponse.Data
                    |> Seq.tryHead
                    |> Option.map (fun o -> 
                                    {
                                        OrderBookTickerInfo.AskPrice = o.BestAskPrice
                                        AskQty   = o.BestAskQuantity
                                        BidPrice = o.BestBidPrice
                                        BidQty   = o.BestBidQuantity
                                        Symbol   = Symbol s
                                    })
                    |> (fun ob ->
                            match ob with
                            | Some v -> Result.Ok v
                            | None -> Result.Error "No orderbook data found"
                        ) 
    }
 
let private exchangeName = "BinanceFutures"

let private getPositions (symbolFilter: Symbol option) =
    async {
        let client = getBaseClient()
        let! result =
            match symbolFilter with
            | Some (Symbol s) ->
                if s.EndsWith "USDT"
                then PositionListener.getUsdtPositionsFromBinanceAPI client.FuturesUsdt symbolFilter
                else PositionListener.getCoinPositionsFromBinanceAPI client.FuturesCoin symbolFilter
            | None ->
                async {
                    let! usdtPositions = PositionListener.getUsdtPositionsFromBinanceAPI client.FuturesUsdt None
                    let! coinmPositions = PositionListener.getCoinPositionsFromBinanceAPI client.FuturesCoin None
                    return (usdtPositions |> Seq.append coinmPositions)
                }

        return Ok (result)
    }

let getExchange() = {
        new IFuturesExchange with
        member __.PlaceOrder o = placeOrder o
        member __.QueryOrder o = queryOrderStatus o
        member __.CancelOrder o = cancelOrder o
        member __.GetOrderBookCurrentPrice s = getOrderBookCurrentPrice s
        member __.Id = Types.ExchangeId ExchangeId
        member __.Name = exchangeName

        member __.GetFuturesPositions symbolFilter = getPositions symbolFilter
        member __.TrackPositions (agent) = async { 
                let started = PositionListener.trackPositions agent
                Log.Information ("Started Binance position tracker : {Success}", started)
                return ()
            }
        member __.GetSupportedSymbols () =
            Seq.concat [Common.usdtSymbols; Common.coinMSymbols]
            |> Seq.map (fun kv -> (kv.Key, kv.Value))
            |> dict
    }