module Binance.Futures.Trade

open Binance.Net
open Binance.Net.Objects.Futures.FuturesData

open System
open Binance.ApiTypes
open Types
open Binance.Net.Interfaces.SubClients.Futures
open Serilog

let ExchangeId = 4L

// ugly: TODO get config from outside?
let private cfg = appConfig.GetSection "Binance"

let getApiKeyCfg () = 
    {
        BinanceApiKey.Key = cfg.Item "FuturesKey"
        Secret = cfg.Item "FuturesSecret"
    }

let getBaseClient () =
    let apiKey = getApiKeyCfg ()
    let options = 
        // this is options for everything - though it has 'Spot' in its name
        let opts = 
            new Objects.Spot.BinanceClientOptions(
                TradeRulesBehaviour = Enums.TradeRulesBehaviour.AutoComply,
                ApiCredentials = new CryptoExchange.Net.Authentication.ApiCredentials(apiKey.Key, apiKey.Secret)
            )
        // a little ugly - but will do for now
        let futuresCoinMBaseUrl = cfg.Item "FuturesCoinMBaseUrl" // Can be used to configure 'testnet'. Leave empty for default / prod
        let futuresUsdtBaseUrl = cfg.Item "FuturesUsdtBaseUrl" // Can be used to configure 'testnet'. Leave empty for default / prod
        if not <| String.IsNullOrWhiteSpace futuresCoinMBaseUrl
        then opts.BaseAddressCoinFutures <- futuresCoinMBaseUrl
            
        if not <| String.IsNullOrWhiteSpace futuresUsdtBaseUrl
        then opts.BaseAddressUsdtFutures <- futuresUsdtBaseUrl

        opts

    let binanceOptions = new BinanceClient(options)
    Log.Information("Using Binance URLs: coin-m = {FuturesCoinMBaseUrl}, usdt = {FuturesUsdtBaseUrl}", 
        options.BaseAddressCoinFutures, options.BaseAddressUsdtFutures)

    binanceOptions

let getSocketClient () =
    let apiKey = getApiKeyCfg ()
    let options = 
        Objects.Spot.BinanceSocketClientOptions ( // though this says 'Spot', it is really futures
                ApiCredentials = new CryptoExchange.Net.Authentication.ApiCredentials(apiKey.Key, apiKey.Secret),
                AutoReconnect = true
            )
    let futuresWssUrl = cfg.Item "FuturesWSSUrl"
    if not <| String.IsNullOrWhiteSpace futuresWssUrl
    then
        options.BaseAddressCoinFutures <- futuresWssUrl
        options.BaseAddressUsdtFutures <- futuresWssUrl

    let socketClient = new BinanceSocketClient (options)
    socketClient

type FuturesMode = USDT | COINM

let private getClient futuresMode =
    let c = getBaseClient ()
    match futuresMode with
    | USDT -> c.FuturesUsdt.Order :> IBinanceClientFuturesOrders
    | COINM  -> c.FuturesCoin.Order :> IBinanceClientFuturesOrders

let private toOrderInfoResult (orderData: BinanceFuturesPlacedOrder) =

    let orderStatus = 
        match orderData.Status with
        | Enums.OrderStatus.New             -> OrderNew
        | Enums.OrderStatus.Canceled        -> OrderCancelled (Qty orderData.ExecutedQuantity, Price orderData.AvgPrice)
        | Enums.OrderStatus.Expired         -> OrderCancelled (Qty 0M, Price 0M)
        | Enums.OrderStatus.Filled          -> OrderFilled (Qty orderData.ExecutedQuantity, Price orderData.AvgPrice)
        | Enums.OrderStatus.PartiallyFilled -> OrderPartiallyFilled(Qty orderData.ExecutedQuantity, Price orderData.AvgPrice)
        | Enums.OrderStatus.Rejected        -> OrderCancelled (Qty 0M, Price 0M)
        | _                                 -> OrderQueryFailed(sprintf "Unrecognised order status: %A" orderData.Status)

    {
        OrderInfo.OrderId = OrderId (string orderData.OrderId)
        ClientOrderId = ClientOrderId orderData.ClientOrderId
        ExecutedQuantity = Qty orderData.ExecutedQuantity
        Time = orderData.UpdateTime |> DateTimeOffset
        Quantity = Qty orderData.OriginalQuantity
        Price = Price orderData.AvgPrice
        Symbol = Symbol orderData.Symbol
        Status = orderStatus
    } |> Ok

let private getFuturesMode (symbol: string) =
    if symbol.EndsWith("usdt", StringComparison.OrdinalIgnoreCase)
    then USDT
    else COINM

let private orderTypeFrom (ot: OrderType) = 
    match ot with
    | LIMIT -> Enums.OrderType.Limit
    | _ -> Enums.OrderType.Market

let private placeOrder (o: OrderInputInfo) : Async<Result<OrderInfo, OrderError>> =
    let (Symbol s) = o.Symbol
    let client = getClient (getFuturesMode s)

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
            then toOrderInfoResult orderResponse.Data
            else 
                Error(OrderError(sprintf "%A: %s" orderResponse.Error.Code orderResponse.Error.Message))
        return result
    }

let private queryOrderStatus (o: OrderQueryInfo) =
    let (Symbol symbol) = o.Symbol
    let client = getClient (getFuturesMode symbol)
    let (OrderId sOrderId) = o.OrderId

    let parsed, orderId = Int64.TryParse sOrderId
    if not parsed then raise <| exn (sprintf "Invalid orderId. Expecting an integer. Found: %s" sOrderId)
    async {
        let! orderResponse = client.GetOrderAsync (symbol, orderId) |> Async.AwaitTask
        if orderResponse.Success
        then 
            let order = orderResponse.Data
            let orderStatus = 
                match order.Status with
                | Enums.OrderStatus.New             -> OrderNew
                | Enums.OrderStatus.Canceled        -> OrderCancelled (Qty order.ExecutedQuantity, Price order.AvgPrice)
                | Enums.OrderStatus.Expired         -> OrderCancelled (Qty 0M, Price 0M)
                | Enums.OrderStatus.Filled          -> OrderFilled (Qty order.ExecutedQuantity, Price order.AvgPrice)
                | Enums.OrderStatus.PartiallyFilled -> OrderPartiallyFilled(Qty order.ExecutedQuantity, Price order.AvgPrice)
                | Enums.OrderStatus.Rejected        -> OrderCancelled (Qty 0M, Price 0M)
                | _                                 -> OrderQueryFailed(sprintf "Unrecognised order status: %A" order.Status)
            return orderStatus
        else
            return OrderQueryFailed (sprintf "%A: %s" orderResponse.Error.Code orderResponse.Error.Message)
    }

let private cancelOrder (o: OrderQueryInfo) =
    let (Symbol symbol) = o.Symbol
    let client = getClient (getFuturesMode symbol)
    let (OrderId sOrderId) = o.OrderId

    let parsed, orderId = Int64.TryParse sOrderId
    if not parsed then raise <| exn (sprintf "Invalid orderId. Expecting an integer. Found: %s" sOrderId)
    async {
        let! cancelResponse = client.CancelOrderAsync (symbol, orderId) |> Async.AwaitTask
        return 
            if cancelResponse.Success
            then Ok ()
            else Error (sprintf "%A: %s" cancelResponse.Error.Code cancelResponse.Error.Message)
    }

let private getOrderBookCurrentPrice (Symbol s) =
    let client = getBaseClient ()
    // ugly copy paste of a large section of code - because the library we use doesn't unify the types that pull OrderBook for
    // COIN-M vs USDT futures
    // and I didn't bother to write an abstraction over it.
    async {
        let futuresMode = getFuturesMode s
        match futuresMode with
        | USDT ->
            let! bookResponse = client.FuturesUsdt.Market.GetBookPricesAsync(s) |> Async.AwaitTask
            if not bookResponse.Success
            then Log.Error ("Error getting orderbook from Binance futures: {@Error}", bookResponse.Error)
            return
                if bookResponse.Success
                then 
                    bookResponse.Data
                    |> Seq.tryHead
                    |> Option.map (fun o -> 
                                    {
                                        OrderBookTickerInfo.AskPrice = o.BestAskPrice
                                        AskQty   = o.BestAskQuantity
                                        BidPrice = o.BestBidPrice
                                        BidQty   = o.BestBidQuantity
                                        Symbol   = s
                                    })
                else None

        | COINM ->
            let! bookResponse = client.FuturesCoin.Market.GetBookPricesAsync(s) |> Async.AwaitTask
            if not bookResponse.Success
            then Log.Error ("Error getting orderbook from Binance futures: {@Error}", bookResponse.Error)
            return
                if bookResponse.Success
                then 
                    bookResponse.Data
                    |> Seq.tryHead
                    |> Option.map (fun o -> 
                                    {
                                        OrderBookTickerInfo.AskPrice = o.BestAskPrice
                                        AskQty   = o.BestAskQuantity
                                        BidPrice = o.BestBidPrice
                                        BidQty   = o.BestBidQuantity
                                        Symbol   = s
                                    })
                else None
    }

let getExchange() = {
        new IExchange with
        member __.PlaceOrder o = placeOrder o
        member __.QueryOrder o = queryOrderStatus o
        member __.CancelOrder o = cancelOrder o
        member __.GetOrderBookCurrentPrice s = getOrderBookCurrentPrice (Symbol s)
    }