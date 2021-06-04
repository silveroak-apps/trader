module Bybit.Futures.Trade

open Types
open Bybit.Futures.Common
open Exchanges.Common
open System

let private cfg = appConfig.GetSection "ByBit"

let getApiKeyCfg () =
    { ApiKey.Key = cfg.Item "FuturesKey"
      Secret = cfg.Item "FuturesSecret" }

// let o =
//     { OrderSide = Types.OrderSide.BUY
//       OrderType = Types.OrderType.LIMIT
//       PositionSide = Types.PositionSide.LONG
//       Price = 100M<price>
//       Quantity = 1M<qty>
//       SignalId = 1212L
//       Symbol = Symbol "BTCUSD" }

let private orderTypeFrom (ot: OrderType) =
    match ot with
    | LIMIT -> "limit"
    | MARKET -> "market"

let private orderSideFrom (os: OrderSide) =
    match os with
    | BUY -> "Buy"
    | SELL -> "Sell"
    | s -> failwith <| sprintf "Invalid order side: %A" s

let private getAPISign secret= 
    

let private getBybitClientCfg () =
    let apiKey = getApiKeyCfg () 
    let config = BybitConfig.Default
    config.AddApiKey (apiKey.Key, apiKey.Secret)
    config.AddApiKeyPrefix ("api_key", "")
    config.AddApiKeyPrefix ("sign", "")
    config.AddApiKeyPrefix ("timestamp", "")
    config
        
let placeOrder (o: OrderInputInfo) : Async<Result<OrderInfo, OrderError>> =

    async {
        
        let (Symbol symbol) = o.Symbol

        let config = getBybitClientCfg ()
            

        let timeInForce = "GoodTillCancel"
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
                    orderLinkId = string o.SignalId
                )
            | USDT ->
                let client = BybitUSDTApi(config)
                client.LinearOrderNewAsync(
                    symbol, 
                    orderSideFrom o.OrderSide,
                    orderTypeFrom o.OrderType,
                    timeInForce,
                    float <| (o.Quantity / 1M<qty>),
                    float (o.Price / 1M<price>),
                    orderLinkId = string o.SignalId
                )

        let! response = responseTask |> Async.AwaitTask
        let jobj = response :?> Newtonsoft.Json.Linq.JObject

        let orderResponse = jobj.ToObject<BybitOrderResponse>() // TODO do both COINM/USDT return the same type?
        printfn "response:\n%A" orderResponse
        return (Result.Error(OrderError "NOT IMPLEMENTED YET"))
    }
    
// let private queryOrderStatus (o: OrderQueryInfo) =
//     let (Symbol symbol) = o.Symbol
//     let config = getBybitClientCfg ()
//     let client = BybitCoinMApi(config)
 
//     let (OrderId sOrderId) = o.OrderId

//     let parsed, orderId = Int64.TryParse sOrderId
//     if not parsed then raise <| exn (sprintf "Invalid orderId. Expecting an integer. Found: %s" sOrderId)
//     async {
//         let! orderResponse = client.OrderGetOrdersAsync (symbol, orderId) |> Async.AwaitTask
//         if orderResponse.Success
//         then 
//             let order = orderResponse.Data
//             return mapOrderStatus {| 
//                                     Status = order.Status
//                                     ExecutedQuantity = order.ExecutedQuantity
//                                     AvgPrice = order.AvgPrice |}
//         else
//             return OrderQueryFailed (sprintf "%A: %s" orderResponse.Error.Code orderResponse.Error.Message)
//     }   
let getExchange () =
    { new IFuturesExchange with
        member __.Id = Types.ExchangeId Common.ExchangeId
        member __.Name = "BybitFutures"

        member __.CancelOrder(o: OrderQueryInfo) : Async<Result<bool, string>> = failwith "Not Implemented"
        member __.PlaceOrder o = placeOrder o
        member __.QueryOrder(o: OrderQueryInfo) : Async<OrderStatus> = failwith "Not Implemented"

        member __.GetOrderBookCurrentPrice(o: string) : Async<Result<OrderBookTickerInfo, string>> =
            failwith "Not Implemented"

        member __.GetFuturesPositions(o: Symbol option) : Async<Result<seq<ExchangePosition>, string>> =
            failwith "Not Implemented"

        member __.TrackPositions(agent, symbols) =
            PositionListener.trackPositions agent symbols }
