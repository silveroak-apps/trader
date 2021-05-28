module Bybit.Futures.Trade
open AnalysisTypes
open FsToolkit.ErrorHandling
open System
open Bybit.Futures
open Types
open Exchanges.Common
open IO.Swagger.Client
open System.Collections.Generic

let private cfg = appConfig.GetSection "ByBit"

let getApiKeyCfg () = 
    {
        ApiKey.Key = cfg.Item "FuturesKey"
        Secret = cfg.Item "FuturesSecret"
    }

let apiClient = ApiClient()
let dict = Dictionary<string, string>()
let o = {
        OrderSide = Types.OrderSide.BUY
        OrderType = Types.OrderType.LIMIT
        PositionSide = Types.PositionSide.LONG
        Price = 100M<price>
        Quantity = 1M<qty>
        SignalCommandId = 1212L
        Symbol = Symbol "BTCUSD"
    }

let placeOrder (o: OrderInputInfo) =  //: Async<Result<OrderInfo, OrderError>> =
    let s = o.Symbol
    async {
        let client = BybitCoinMApi() 
        let! response = 
            client.OrderNewAsync("Buy", "BTCUSD", 
                "Limit", o.Quantity / 1M<qty>, 
                "GoodTillCancel", float (o.Price / 1M<price>), 
                orderLinkId = string o.SignalCommandId)
            |> Async.AwaitTask

        let jobj = response :?> Newtonsoft.Json.Linq.JObject
        let orderResponse = jobj.ToObject<BybitOrderResponse>()
        printfn "response:\n%A" orderResponse
        return ()
    }


// let getExchange() = {
//         new IExchange with
//         member __.PlaceOrder o = placeOrder o
//         member __.QueryOrder o = queryOrderStatus o
//         member __.CancelOrder o = cancelOrder o
//         member __.GetOrderBookCurrentPrice s = getOrderBookCurrentPrice (Symbol s)
//     }