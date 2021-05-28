module Binance.Futures.Trade

open Binance.Net
open Binance.Net.Objects.Futures.FuturesData

open System
open Binance.ApiTypes
open Types
open Binance.Net.Interfaces.SubClients.Futures
open Serilog
open Exchanges.Common

let ExchangeId = 4L

// ugly: TODO get config from outside?
let private cfg = appConfig.GetSection "Binance"

let getApiKeyCfg () = 
    {
        ApiKey.Key = cfg.Item "FuturesKey"
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
    Log.Verbose("Using Binance URLs: coin-m = {FuturesCoinMBaseUrl}, usdt = {FuturesUsdtBaseUrl}", 
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

let private toOrderInfoResult (orderData: BinanceFuturesPlacedOrder) =
    {
        OrderInfo.OrderId = OrderId (string orderData.OrderId)
        ClientOrderId = ClientOrderId orderData.ClientOrderId
        ExecutedQuantity = Qty orderData.ExecutedQuantity
        Time = orderData.UpdateTime |> DateTimeOffset
        Quantity = Qty orderData.OriginalQuantity
        Price = Price orderData.AvgPrice
        Symbol = Symbol orderData.Symbol
        Status = 
            mapOrderStatus {| 
                                Status = orderData.Status
                                ExecutedQuantity = orderData.ExecutedQuantity
                                AvgPrice = orderData.AvgPrice |}
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
                    newClientOrderId = string o.SignalCommandId,
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
// TODO: We might need to handle some errors that can't be retried
// and we need to indicate to the caller that they need to change the inputs
(* for example:
-2018 BALANCE_NOT_SUFFICIENT
    Balance is insufficient.
-2019 MARGIN_NOT_SUFFICIEN
    Margin is insufficient.
-2020 UNABLE_TO_FILL
    Unable to fill.
-2021 ORDER_WOULD_IMMEDIATELY_TRIGGER
    Order would immediately trigger.
-2022 REDUCE_ONLY_REJECT
    ReduceOnly Order is rejected.
-2023 USER_IN_LIQUIDATION
    User in liquidation mode now.
-2024 POSITION_NOT_SUFFICIENT
    Position is not sufficient.
-2025 MAX_OPEN_ORDER_EXCEEDED
    Reach max open order limit.
-2026 REDUCE_ONLY_ORDER_TYPE_NOT_SUPPORTED
    This OrderType is not supported when reduceOnly.
-2027 MAX_LEVERAGE_RATIO
    Exceeded the maximum allowable position at current leverage.
-2028 MIN_LEVERAGE_RATIO
    Leverage is smaller than permitted: insufficient margin balance.
*) 
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
            return mapOrderStatus {| 
                                    Status = order.Status
                                    ExecutedQuantity = order.ExecutedQuantity
                                    AvgPrice = order.AvgPrice |}
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
        let futuresMode = getFuturesMode s
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

let getExchange() = {
        new IFuturesExchange with
        member __.PlaceOrder o = placeOrder o
        member __.QueryOrder o = queryOrderStatus o
        member __.CancelOrder o = cancelOrder o
        member __.GetOrderBookCurrentPrice s = getOrderBookCurrentPrice (Symbol s)
        member __.Id = Types.ExchangeId ExchangeId
        member __.Name = "Binance-Futures"

        member __.GetFuturesPositions _symbolFilter = async { return (Error "NOT IMPLEMENTED YET") }
        member __.TrackPositions _agent = async { return () }
    }