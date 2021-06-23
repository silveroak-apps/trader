module Bybit.Futures.Trade

open Types
open Bybit.Futures.Common
open Exchanges.Common
open System
open FsToolkit.ErrorHandling
open FSharp.Linq

open Serilog

let private orderTypeFrom (ot: OrderType) =
    match ot with
    | LIMIT -> "Limit"
    | MARKET -> "Market"

let private orderSideFrom (os: OrderSide) =
    match os with
    | BUY -> "Buy"
    | SELL -> "Sell"
    | s -> failwith <| sprintf "Invalid order side: %A" s
    
let private mapOrderStatus 
    (order: {| 
                Status: string
                ExecutedQuantity: decimal
                AvgPrice: decimal
            |}
    ) =
    match order.Status with
    | "Created"         -> OrderNew
    | "New"             -> OrderNew
    | "Cancelled"       -> OrderCancelled (Qty order.ExecutedQuantity, Price order.AvgPrice)
    | "Filled"          -> OrderFilled (Qty order.ExecutedQuantity, Price order.AvgPrice)
    | "Triggered"       -> OrderPartiallyFilled(Qty order.ExecutedQuantity, Price order.AvgPrice)
    | "Rejected"        -> OrderCancelled (Qty 0M, Price 0M)
    | "Untriggered"     -> OrderCancelled (Qty 0M, Price 0M)
    | status            -> OrderQueryFailed (sprintf "Unrecognised order status: %A" status)

let private toOrderInfoResult (orderData: ByBitOrderResponseResult) =
    {
        OrderInfo.OrderId = OrderId orderData.OrderId
        ClientOrderId = ClientOrderId orderData.OrderLinkId
        ExecutedQuantity = Qty (decimal orderData.CumExecQty)
        Time = orderData.LastExecTime.GetValueOrDefault() 
                |> int64 
                |> DateTimeOffset.FromUnixTimeSeconds

        Quantity = Qty (decimal orderData.Qty)
        Price = orderData.Price.GetValueOrDefault() |> decimal |> Price
       
        Symbol = Symbol orderData.Symbol
        Status = 
            mapOrderStatus {| 
                            Status = orderData.OrderStatus
                            ExecutedQuantity = decimal orderData.CumExecQty
                            AvgPrice = orderData.Price.GetValueOrDefault() |> decimal |} //TODO: Need to check for price or Last executed price
    } |> Ok    
        
let placeOrder (o: OrderInputInfo) : Async<Result<OrderInfo, OrderError>> =

    async {
        let (Symbol symbol) = o.Symbol
        let timeInForce = "PostOnly" // For maker fees
        let orderLinkId = sprintf "%d_%d" o.SignalCommandId (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        let reduceOnly = 
            match o.PositionSide, o.OrderSide with
            | PositionSide.LONG, OrderSide.SELL     -> true
            | PositionSide.SHORT, OrderSide.BUY     -> true
            | _,_                                   -> false
        
        let orderSide = orderSideFrom o.OrderSide
        let orderType = orderTypeFrom o.OrderType
        let price = float (o.Price / 1M<price>)
        
        let responseTask =
            match getFuturesMode o.Symbol with
            | COINM -> 
                let qty = int <| o.Quantity / 1M<qty>
                
                Log.Debug("Bybit placeOrder (signalCommand: {CommandId}): {OrderSide}, {OrderType}, {Qty} @ {Price}", 
                    o.SignalCommandId,
                    orderSide, orderType, qty, price, reduceOnly)

                coinMClient.OrderNewAsync(
                    orderSide,
                    symbol,
                    orderType,
                    qty,
                    timeInForce,
                    price = price,
                    reduceOnly = reduceOnly,
                    orderLinkId = orderLinkId
                )
            | USDT ->

                let qty = float <| o.Quantity / 1M<qty>

                Log.Debug("Bybit placeOrder (signalCommand: {CommandId}): {OrderSide}, {OrderType}, {Qty} @ {Price}, reduceOnly: {ReduceOnly}.", 
                    o.SignalCommandId,
                    orderSide, orderType, qty, price, reduceOnly)

                usdtClient.LinearOrderNewAsync(
                    symbol, 
                    orderSideFrom o.OrderSide,
                    orderTypeFrom o.OrderType,
                    timeInForce,
                    qty,
                    price,
                    reduceOnly = reduceOnly, 
                    closeOnTrigger = false,
                    orderLinkId = orderLinkId
                )
        
        let! response = responseTask |> Async.AwaitTask
        
        let jobj = response :?> Newtonsoft.Json.Linq.JObject
        let orderResponse = jobj.ToObject<BybitOrderResponse>()
        Log.Debug("Bybit placeOrder response: {Response}", orderResponse)
        let result = 
            if orderResponse.RetCode ?= 0M
            then 
                let jResultObj    = orderResponse.Result :?> Newtonsoft.Json.Linq.JObject
                let orderResponse = jResultObj.ToObject<ByBitOrderResponseResult>()
                toOrderInfoResult orderResponse
            else 
                Error(OrderError(sprintf "%A: %s" orderResponse.RetCode orderResponse.RetMsg))
        return result        
    }
    
let queryOrderStatus (o: OrderQueryInfo) =
    
    let (Symbol symbol) = o.Symbol
    let (OrderId sOrderId) = o.OrderId

    async {
        let responseTask =
            match getFuturesMode o.Symbol with
            | COINM -> 
                coinMClient.OrderQueryAsync (sOrderId, symbol)
            | USDT -> 
                usdtClient.LinearOrderQueryAsync (symbol, sOrderId) 

        let! response = responseTask |> Async.AwaitTask
        let jobj = response :?> Newtonsoft.Json.Linq.JObject
        let orderResponse = jobj.ToObject<BybitOrderResponse>()
        Log.Debug("Bybit queryOrder response: {Response}", orderResponse)
        if orderResponse.RetCode ?= 0M
        then 
            let jResultObj    = orderResponse.Result :?> Newtonsoft.Json.Linq.JObject
            let orderResponse = jResultObj.ToObject<ByBitOrderResponseResult>()
            return mapOrderStatus {| 
                                    Status = orderResponse.OrderStatus
                                    ExecutedQuantity = decimal orderResponse.CumExecQty
                                    AvgPrice = orderResponse.LastExecPrice.GetValueOrDefault() |> decimal |}
        else
            return OrderQueryFailed (sprintf "%A: %s" orderResponse.RetCode orderResponse.RetMsg)
    } 
    
let cancelOrder (o: OrderQueryInfo) =
    let (Symbol symbol) = o.Symbol
    let (OrderId sOrderId) = o.OrderId

    async {
        let responseTask =
            match getFuturesMode o.Symbol with
            | COINM -> 
                coinMClient.OrderCancelAsync (symbol = symbol, orderId = sOrderId)
            | USDT -> 
                usdtClient.LinearOrderCancelAsync (symbol = symbol, orderId = sOrderId)
                
        let! cancelResponse = responseTask |> Async.AwaitTask
        let jobj = cancelResponse :?> Newtonsoft.Json.Linq.JObject
        let orderResponse = jobj.ToObject<BybitOrderResponse>()

        Log.Debug("Bybit cancelOrder response: {Response}", orderResponse)
        return 
            if orderResponse.RetCode ?= 0M
            then Ok true
            elif orderResponse.RetCode ?= 130010M // order not exists or too late to cancel: happens when order was already filled by the time cancellation was attempted
              || orderResponse.RetCode ?= 130037M // order already cancelled (usdt)
              || orderResponse.RetCode ?= 30032M  // already filled or cancelled (coin-m)
              || orderResponse.RetCode ?= 30037M  // already cancelled (coin-m)
            then Ok false
            else Error (sprintf "%A: %s" orderResponse.RetCode orderResponse.RetMsg)
    }
    
let getExchange () =
    { new IFuturesExchange with
        member __.Id = Types.ExchangeId Common.ExchangeId
        member __.Name = Common.ExchangeName

        member __.CancelOrder o = cancelOrder o
        member __.PlaceOrder o = placeOrder o
        member __.QueryOrder o = queryOrderStatus o

        member __.GetOrderBookCurrentPrice s = Bybit.Futures.Common.getOrderBookCurrentPrice s

        member __.GetFuturesPositions symbolFilter = PositionListener.getPositions symbolFilter
        member __.TrackPositions agent = PositionListener.trackPositions agent
        member __.GetSupportedSymbols () =
            Seq.concat [Common.usdtSymbols; Common.coinMSymbols]
            |> Seq.map (fun kv -> (kv.Key, kv.Value))
            |> dict
    }
