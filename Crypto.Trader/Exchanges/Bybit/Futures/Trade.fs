module Bybit.Futures.Trade

open Types
open Bybit.Futures.Common
open Exchanges.Common
open System
open FsToolkit.ErrorHandling

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
        let timeInForce = "GoodTillCancel" // For maker fees
        let responseTask =
            match getFuturesMode o.Symbol with
            | COINM -> 
                let client = BybitCoinMApi(config)
                client.OrderNewAsync(
                    orderSideFrom o.OrderSide,
                    symbol,
                    orderTypeFrom o.OrderType,
                    o.Quantity / 1M<qty>,
                    timeInForce,
                    price = float (o.Price / 1M<price>),
                    orderLinkId = string o.SignalCommandId
                )
            | USDT ->
                let client = BybitUSDTApi(config)
                (*
                    string side = "Buy";
                    string symbol = "BTCUSDT";
                    string orderType = "Limit";
                    double? qty = 20;
                    string timeInForce = "GoodTillCancel";
                    double? price = 20;
                    double? takeProfit = null;
                    double? stopLoss = null;
                    bool? reduceOnly = null;
                    bool? closeOnTrigger = null;
                    string orderLinkId = "abcd";
                    string tpTriggerBy = null;
                    string slTriggerBy = null;
                    IO.Swagger.Api.LinearOrderApi.LinearOrderNewAsync(
                        ?symbol: string,
                        ?side: string,
                        ?orderType: string,
                        ?timeInForce: string,
                        ?qty: Nullable<float>,
                        ?price: Nullable<float>,
                        ?takeProfit: Nullable<float>,
                        ?stopLoss: Nullable<float>,
                        ?reduceOnly: Nullable<bool>,
                        ?tpTriggerBy: string,
                        ?slTriggerBy: string,
                        ?closeOnTrigger: Nullable<bool>,
                        ?orderLinkId: string) : Threading.Tasks.Task<obj>
                    *)
                client.LinearOrderNewAsync(
                    symbol, 
                    orderSideFrom o.OrderSide,
                    orderTypeFrom o.OrderType,
                    timeInForce,
                    float <| (o.Quantity / 1M<qty>),
                    float (o.Price / 1M<price>),
                    reduceOnly = false, //TODO Need to update with position and order side Buy Short - true Sell Long true 
                    closeOnTrigger = false,
                    orderLinkId = string o.SignalCommandId
                )
        
        let! response = responseTask |> Async.AwaitTask
        
        let jobj = response :?> Newtonsoft.Json.Linq.JObject
        let orderResponse = jobj.ToObject<BybitOrderResponse>()
        printfn "response:\n%A" orderResponse //TODO: For debug, remove it later..
        let result = 
            if orderResponse.RetCode = Nullable 0M
            then 
                let jResultObj      = orderResponse.Result :?> Newtonsoft.Json.Linq.JObject
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
                let client = BybitCoinMApi(config)
                client.OrderQueryAsync (symbol, sOrderId)
            | USDT -> 
                let client = BybitUSDTApi(config)
                client.LinearOrderQueryAsync (symbol, sOrderId) 

        let! response = responseTask |> Async.AwaitTask
        let jobj = response :?> Newtonsoft.Json.Linq.JObject
        let orderResponse = jobj.ToObject<BybitOrderResponse>()
        printfn "query response:\n%A" orderResponse //TODO: For debug, remove it later..
        if Nullable.Equals(orderResponse.RetCode, 0M)
        then 
            let jResultObj      = orderResponse.Result :?> Newtonsoft.Json.Linq.JObject
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
                let client = BybitCoinMApi(config)
                client.OrderCancelAsync (symbol = symbol, orderId = sOrderId)
            | USDT -> 
                let client = BybitUSDTApi(config)
                client.LinearOrderCancelAsync (symbol = symbol, orderId = sOrderId)
                
        let! cancelResponse = responseTask |> Async.AwaitTask
        let jobj = cancelResponse :?> Newtonsoft.Json.Linq.JObject
        let orderResponse = jobj.ToObject<BybitOrderResponse>()

        printfn "cancel response:\n%A" orderResponse //TODO: For debug, remove it later..
        return 
            if Nullable.Equals(orderResponse.RetCode, 0M)
            then Ok true
            else Error (sprintf "%A: %s" orderResponse.RetCode orderResponse.RetMsg)
    }
    
let getOrderBookCurrentPrice (Symbol s) =
    let client = BybitMarketApi(config)
    // ugly copy paste of a large section of code - because the library we use doesn't unify the types that pull OrderBook for
    // COIN-M vs USDT futures
    // and I didn't bother to write an abstraction over it.
    async {
        let! response = client.MarketOrderbookAsync (s) |> Async.AwaitTask
        let jobj = response :?> Newtonsoft.Json.Linq.JObject
        let bookResponse = jobj.ToObject<ByBitOBResponse>()
        printfn "book response:\n%A" bookResponse //TODO: For debug, remove it later..
        if not (Nullable.Equals(bookResponse.RetCode, 0M))
        then 
            return Result.Error (sprintf "Error getting orderbook: %A: %s" bookResponse.RetCode bookResponse.RetMsg)
        else
            let bestBid = 
                bookResponse.Result
                |> Seq.filter (fun b -> b.Side = "Buy")
                |> Seq.tryHead
            let bestAsk = 
                bookResponse.Result
                |> Seq.filter (fun b -> b.Side = "Sell")
                |> Seq.tryHead
            return
                Option.map2 (fun (b: ByBitOBResultResponse)  (a: ByBitOBResultResponse) -> 
                                {
                                    OrderBookTickerInfo.AskPrice = decimal a.Price
                                    AskQty   = decimal a.Size
                                    BidPrice = decimal b.Price
                                    BidQty   = decimal b.Size
                                    Symbol   = Symbol s
                                }) bestBid bestAsk
                |> (fun ob ->
                        match ob with
                        | Some v -> Result.Ok v
                        | None -> Result.Error "No orderbook data found"
                    ) 

    }

let getExchange () =
    { new IFuturesExchange with
        member __.Id = Types.ExchangeId Common.ExchangeId
        member __.Name = "BybitFutures"

        member __.CancelOrder o = cancelOrder o
        member __.PlaceOrder o = placeOrder o
        member __.QueryOrder o = queryOrderStatus o

        member __.GetOrderBookCurrentPrice s = getOrderBookCurrentPrice s

        member __.GetFuturesPositions symbolFilter = PositionListener.getPositions symbolFilter
        member __.TrackPositions(agent, symbols) = PositionListener.trackPositions agent symbols 
    }
