module Binance.Spot.Trade

open System.Security.Cryptography
open System
open Types
open System.Text
open Serilog

open Binance.ApiTypes
open System.Net.Http

type ValidTradeRange = {
    MinPrice: decimal<price> option
    MaxPrice: decimal<price> option
    PriceTickSize: decimal<price> option

    MinQty: decimal<qty> option
    MaxQty: decimal<qty> option
    QtyLotSize: decimal<qty> option

    MinNotional: decimal<qty * price> option
}

let private apiKey = 
    let cfg = appConfig.GetSection "Binance"
    { 
        BinanceApiKey.Key = cfg.Item "SpotKey"
        Secret = cfg.Item "SpotSecret"
    }

let getApiKeyCfg () = apiKey

let private httpClient = new HttpClient()

let private buildUri uri queryParams =
    let query = 
        queryParams 
        |> List.map (fun (name, value) -> sprintf "%s=%s" name value)
        |> (fun ss -> System.String.Join("&", ss))
    
    if (not (String.IsNullOrWhiteSpace query)) then sprintf "%s?%s" uri query else uri

//we couldn't get the Http module in FSharp.Data to work with Az functions and .NET Std 2.0
let private httpAsyncRequestString (uri: string, queryParams: (string * string) list, headers: (string * string) list) = 
    let uriWithQuery = buildUri uri queryParams

    httpClient.DefaultRequestHeaders.Clear()
    headers |> List.iter(fun (name, value) -> httpClient.DefaultRequestHeaders.Add(name, value))
    async { 
        let! response = httpClient.GetAsync(uriWithQuery) |> Async.AwaitTask
        let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        
        if (not response.IsSuccessStatusCode)
        then Log.Warning ("Error querying Binance {Uri}: {Response}", uri, responseBody)

        //keeps the previous (ugly) behaviour of throwing an exception, 
        //but we get a nicer log message from the above
        response.EnsureSuccessStatusCode() |> ignore
        return responseBody
    }

let private httpAsyncRequest (uri: string, queryParams: (string * string) list, headers: (string * string) list, method: string, shouldRetry: bool) = 
    let uriWithQuery = buildUri uri queryParams
    let msg = new HttpRequestMessage(HttpMethod method, Uri uriWithQuery)
    httpClient.DefaultRequestHeaders.Clear()
    headers |> List.iter(fun (name, value) -> httpClient.DefaultRequestHeaders.Add(name, value))
    httpClient.SendAsync msg |> Async.AwaitTask

let private binanceExchangeInfo = 
    async {
        let! responseBody = httpAsyncRequestString ("https://api.binance.com/api/v1/exchangeInfo", [], [])
        let response = BinanceExchangeInfoResponse.Parse responseBody
        return response
    } |> Async.RunSynchronously // figure out if there is a nicer flow that we can do here. Will also need to consider redoing the API call once in a few hours

let private tradeRangeFor (symbolPair: string) = 
    let mkTradeRange (fltrs: BinanceFilterInfo seq) = 

        //we know all symbols will have these filter types: see binance.info.json
        let (minQty, maxQty, priceLotSize) = 
            fltrs 
            |> Seq.find (fun fltr -> fltr.FilterType = "LOT_SIZE")
            |> (fun fltr -> fltr.MinQty, fltr.MaxQty, fltr.StepSize)

        let (minPrice, maxPrice, priceTickSize) = 
            fltrs 
            |> Seq.find (fun fltr -> fltr.FilterType = "PRICE_FILTER") 
            |> (fun fltr -> fltr.MinPrice, fltr.MaxPrice, fltr.TickSize)

        let minNotional = 
            fltrs 
            |> Seq.find (fun fltr -> fltr.FilterType = "MIN_NOTIONAL")
            |> (fun fltr -> fltr.MinNotional)
        
        let toQty = Option.map Qty
        let toPrice = Option.map Price

        {
            MinQty = minQty |> toQty
            MaxQty = maxQty |> toQty
            QtyLotSize = priceLotSize |> toQty

            MinPrice = minPrice |> toPrice
            MaxPrice = maxPrice |> toPrice
            PriceTickSize = priceTickSize |> toPrice

            MinNotional = Option.map ((*) 1M<qty * price>) minNotional
        }        

    binanceExchangeInfo.Symbols 
    |> Seq.tryFind (fun sym -> sym.Symbol = symbolPair)
    |> Option.map (fun sym -> sym.Filters |> mkTradeRange)

type Rounding = RoundUp | RoundDown | Default

let private normalise (origValue: decimal<'u>) (min: decimal<'u>) (max: decimal<'u>) (step: decimal<'u> option) (rounding: Rounding) : Result<decimal<'u>, string> = 
    let normalise' step' =
        if (origValue - min) % step' = LanguagePrimitives.DecimalWithMeasure 0M //a bit ugly, but type inference fails otherwise when using a generic unit of measure here
        then origValue
        else if rounding = Rounding.RoundDown
        then (origValue - (origValue % step'))
        else if rounding = Rounding.RoundUp
        then (origValue + (step' - (origValue % step')))
        else
            let stepStr = (step'/LanguagePrimitives.DecimalWithMeasure<'u> 1.00000000M).ToString()
            let stepPrecision = 
                if stepStr.IndexOf(".") < 0
                then 0
                else stepStr.Substring(stepStr.IndexOf(".") + 1).Length
            let res = Decimal.Round (decimal origValue, stepPrecision)
            LanguagePrimitives.DecimalWithMeasure<'u> res
    
    if step.IsSome 
    then 
        let normalised = normalise' step.Value 
        if (normalised >= min && (normalised <= max || max = LanguagePrimitives.DecimalWithMeasure 0M))
        then Ok normalised
        else Error <| sprintf "Normalised value (%M) is outside the range [%M,%M]" normalised min max
    else Ok origValue
  
let private normaliseOrderQuantityAndPrice (origQty: decimal<qty>) (origPrice: decimal<price>) orderSide symbol : Result<decimal<qty> * decimal<price>, string> =
    
    tradeRangeFor symbol
    |> fun maybeTradeRange ->
        if maybeTradeRange.IsNone
        then
            Error <| sprintf "No trading range found for symbol: %s" symbol
        else
            let tradeRange = maybeTradeRange.Value
            let minQty = if tradeRange.MinQty.IsSome then tradeRange.MinQty.Value else 0M<qty>
            let maxQty = if tradeRange.MaxQty.IsSome then tradeRange.MaxQty.Value else System.Decimal.MaxValue * 1M<qty>
            let minPrice = if tradeRange.MinPrice.IsSome then tradeRange.MinPrice.Value else 0M<price>
            let maxPrice = if tradeRange.MaxPrice.IsSome then tradeRange.MaxPrice.Value else System.Decimal.MaxValue * 1M<price>

            let qtyRounding = if orderSide = BUY then Rounding.RoundUp else Rounding.RoundDown
            let normalisedQty = normalise origQty minQty maxQty tradeRange.QtyLotSize qtyRounding

            // we need to do default rounding on Price because price (rate) might not be align to the tick size of binance after we have altered the price
            // we only alter the price if the trade does not go thru the first time
            let normalisedPrice = normalise origPrice minPrice maxPrice tradeRange.PriceTickSize Rounding.Default

            let minNotional = if tradeRange.MinNotional.IsSome then tradeRange.MinNotional.Value else 0M<qty * price>

            match (normalisedQty, normalisedPrice) with
            | Error s, _ -> Error s
            | _, Error s -> Error s
            | Ok nq, Ok np when np * nq < minNotional -> Error <| sprintf "Normalised  Qty (%M) * Normalised Price (%M) is less than min notional required (%M)" nq np minNotional
            | Ok nq, Ok np -> Ok (nq, np)

let private signQueryParams (apiSecret: string)  (queryParams : (string * string) list) =
    let bytesToHex = Array.fold (fun state x-> state + sprintf "%02x" x) ""

    let queryBytes =
        queryParams
        |> List.map (fun (n,v) -> sprintf "%s=%s" n v)
        |> List.fold (fun s item -> if s = "" then item else sprintf "%s&%s" s item) ""
        |> Encoding.UTF8.GetBytes

    use sha256 = new HMACSHA256 (apiSecret |> Encoding.UTF8.GetBytes)
    let hmacSignature = sha256.ComputeHash queryBytes |> bytesToHex

    List.append queryParams [ ("signature", hmacSignature) ]

let private recvWindowMillis = 10M * 1000M

let queryTrades (Symbol symbol) (fromId: int64 option) : Async<Result<BinanceMyTradesResponse[], string>> =
    let rec queryTrades' (acc: Result<BinanceMyTradesResponse[], string>) (nextStartId: int64 option) : Async<Result<BinanceMyTradesResponse[], string>> =
        async {
            try
                match acc with
                | Ok trades ->
                    let fromIdParam = 
                        match nextStartId with
                        | Some id -> [ ("fromId", id |> string) ]
                        | None -> []

                    let queryParams = 
                        fromIdParam @
                        [
                            ("symbol", symbol)
                            ("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() |> string)
                            ("recvWindow", recvWindowMillis |> string)
                            ("limit", 1000 |> string)
                        ] 
                        |> signQueryParams apiKey.Secret

                    let url = "https://api.binance.com/api/v3/myTrades"

                    let! response = httpAsyncRequestString (url, queryParams, [ ("X-MBX-APIKEY", apiKey.Key) ])

                    Log.Debug ("Querying Binance myTrades endpoint with {QueryParams}", queryParams)

                    let moreTrades = response |> BinanceMyTradesResponse.ParseArray 
                    if moreTrades.Length > 0
                    then 
                        Log.Debug ("Got {TradeCountThisApiCall} trades in this API call", moreTrades.Length)
                        // keep querying for more
                        do! Async.Sleep 1000 // rate limits :/
                        let newFromId = Some (moreTrades |> Seq.map (fun o -> o.Id) |> Seq.max |> fun t -> t + 1L)
                        return! queryTrades' (Ok <| Array.append trades moreTrades) newFromId
                    else
                        return Ok trades

                | Error s -> return Error s
            with e ->
                let msg = sprintf "query TRADES for %A - error:\\n%A" symbol e
                Log.Warning msg
                return Error msg
        }

    queryTrades' (Ok [||]) fromId

let toOrderStatus (binanceStatus: string) (executedQty: decimal<qty>) (executedPrice: decimal<price>) =
    match (binanceStatus, executedQty, executedPrice) with
    | ("FILLED", q, p)           -> OrderStatus.OrderFilled (q, p)
    | ("NEW", _, _)              -> OrderNew
    | ("PARTIALLY_FILLED", q, p) -> OrderPartiallyFilled (q, p)
    | ("CANCELED", q, p)         -> OrderCancelled (q, p)
    | (s, _, _)                  -> OrderQueryFailed s

let queryOrders (Symbol symbol) (fromId: int64 option) : Async<Result<BinanceMyOrdersResponse[], string>> =
    let rec queryOrders' (acc: Result<BinanceMyOrdersResponse[], string>) (fromId': int64 option) =
        async {
            try
                match acc with
                | Ok orders ->
                    let fromIdParam = 
                        match fromId' with
                        | Some id -> [ ("fromId", id |> string) ]
                        | None -> []

                    let queryParams = 
                        fromIdParam @
                        [
                            ("symbol", symbol)
                            ("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() |> string)
                            ("recvWindow", recvWindowMillis |> string)
                            ("limit", 1000 |> string)
                        ] 
                        |> signQueryParams apiKey.Secret

                    let url = "https://api.binance.com/api/v3/allOrders"

                    let! response = httpAsyncRequestString (url, queryParams, [ ("X-MBX-APIKEY", apiKey.Key) ])

                    let moreOrders = response |> BinanceMyOrdersResponse.ParseArray 

                    if moreOrders.Length > 0
                    then 
                        // keep querying for more
                        do! Async.Sleep 1000 // rate limits :/
                        let newFromId = Some (moreOrders |> Seq.map (fun o -> o.OrderId) |> Seq.max)
                        return! queryOrders' (Ok <| Array.append orders moreOrders) newFromId
                    else
                        return Ok orders
                | Error s -> 
                    return Error s
            with e ->
                let msg = sprintf "query ORDERs for %A - error:\\n%A" symbol e
                Log.Warning msg
                return Error msg
        }
    queryOrders' (Ok [||]) fromId

let queryOrder (order: OrderQueryInfo) : Async<Result<BinanceOrderQueryResponse, string>> =
    async {

        let (OrderId orderId), (Symbol symbol) =
            order.OrderId, order.Symbol

        try
            let queryParams = 
                [
                    ("symbol", symbol)
                    ("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() |> string)
                    ("recvWindow", recvWindowMillis |> string)
                    ("orderId", orderId |> string)
                ] 
                |> signQueryParams apiKey.Secret

            let url = "https://api.binance.com/api/v3/order"
            
            let! response = httpAsyncRequestString (url, queryParams, [ ("X-MBX-APIKEY", apiKey.Key) ])
            Log.Information <| sprintf "ORDER (%s) query response \\n%s" symbol response
        
            return (Ok <| BinanceOrderQueryResponse.Parse response)
        with e ->
            Log.Warning (e, "ORDER query error: {BinanceOrderId} for {Symbol}", orderId, symbol)
            return (Error <| sprintf "ORDER query error: %A %s" orderId e.Message)
    }

let queryOrderStatus (order: OrderQueryInfo) : Async<OrderStatus> =
    async {
        let (OrderId orderId), (Symbol symbol) =
            order.OrderId, order.Symbol

        match! queryOrder order with   
        | Ok orderResponse ->
            let age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(orderResponse.Time)
            let status = toOrderStatus orderResponse.Status (Qty orderResponse.ExecutedQty) (Price orderResponse.Price)

            match status with
            | OrderStatus.OrderFilled  _ -> 
                Log.Information ("ORDER {BinanceOrderId} for {Symbol} successful!", orderId, symbol)

            | OrderNew ->
                Log.Debug ("ORDER {BinanceOrderId} for {Symbol} is not yet filled ({BinanceStatus}). Order age: {OrderAge}", 
                    orderId, symbol, "OrderNew", age)

            | OrderPartiallyFilled _ ->
                Log.Debug ("ORDER {BinanceOrderId} for {Symbol} is not yet filled ({BinanceStatus}). Order age: {OrderAge}", 
                    orderId, symbol, "OrderPartiallyFilled", age)
                
            | OrderCancelled _ ->
                Log.Warning ("ORDER {BinanceOrderId} for {Symbol} is now ({BinanceStatus}). Executed qty: {ExecutedQty}",
                    orderId, symbol, orderResponse.ExecutedQty)

            | OrderQueryFailed s ->
                Log.Warning ("Error querying order {BinanceOrderId} for {Symbol} or interpreting status. Returned status: {BinanceStatus}", s)
            
            return status

        | Error s -> 
            return (OrderQueryFailed s)
    }

let cancelOrder (order: OrderQueryInfo)  = 
    let timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    let (OrderId orderId), (Symbol symbol) = 
        order.OrderId, order.Symbol

    let queryParams = 
        [
            ("symbol", symbol);
            ("timestamp", timestamp |> string);
            ("orderId", orderId |> string);
            ("recvWindow", recvWindowMillis |> string);
        ] 
        |> signQueryParams apiKey.Secret
    let url = "https://api.binance.com/api/v3/order"

    async {
        let! response = httpAsyncRequest (url, queryParams, [ ("X-MBX-APIKEY", apiKey.Key) ], "DELETE", true)
        Log.Information <| sprintf "  - Binance cancel order %s Response Status: %s" symbol (response.StatusCode.ToString())
        let! msg = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        match response.StatusCode with
        | System.Net.HttpStatusCode.OK -> return (Ok true)
        | _ -> return (Error <| sprintf "Could not cancel order %s. Response: %s" orderId msg)
    }

let placeOrder' (signalId: int64) (orderSide: OrderSide) (symbol: Symbol) (quantity: decimal<qty>) (price: decimal<price>) : Async<Result<BinanceOrderSuccessResponse, string>> = 
    //let getServerTimestamp () = 
    //    let serverTime = Http.RequestString "https://api.binance.com/api/v1/time" |> BinanceServerTime.Parse
    //    log "%A" serverTime.ServerTime
    //    serverTime.ServerTime
    //let timestamp = getServerTimestamp ()

    async {
            let (Symbol sym) = symbol
            let timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            let queryParams = 
                [
                    ("symbol", sym)
                    ("side", orderSide |> string)
                    ("type", "LIMIT")
                    ("timeInForce", "GTC")
                    ("timestamp", timestamp |> string)
                    ("quantity", quantity |> sprintf "%.6f")
                    ("price", price |> sprintf "%.8f")
                    ("newClientOrderId", signalId |> string)
                    ("recvWindow", recvWindowMillis |> string)
                ] 
                |> signQueryParams apiKey.Secret

            try
                Log.Information <| sprintf "Placing %A order - %M %s @ %M" orderSide quantity sym price
                Log.Information <| sprintf "  - Request params: %A" queryParams 
                let url = "https://api.binance.com/api/v3/order"
                let! httpResponse = httpAsyncRequest (url, queryParams, [ ("X-MBX-APIKEY", apiKey.Key) ], "POST", false)
                Log.Information <| sprintf "  - Binance %A %s Response Status: %A. Order (qty, price): %M @ %M" orderSide sym httpResponse.StatusCode quantity price 

                let! responseBody = httpResponse.Content.ReadAsStringAsync() |> Async.AwaitTask

                match (httpResponse.StatusCode |> LanguagePrimitives.EnumToValue) with
                | x when x >= 200 && x < 300 -> 
                    let response = BinanceOrderSuccessResponse.Parse responseBody
                    return (response |> Result.mapError string)

                | 400 -> 
                    let msg = sprintf "Binance %A order 400 response msg :%s" orderSide responseBody
                    Log.Error msg
                    return (Error msg)
            
                | x -> 
                    let msg = sprintf "Unknown response from Binance: %A %s" x responseBody
                    Log.Error msg
                    return (Error msg)
            
            with e ->
                let msg = sprintf "Error placing {OrderSide} order for {Symbol}. Quantity: %M. Price: %M" quantity price
                Log.Warning (e, msg, orderSide, sym)
                return (Error msg)
    }

let getCurrentPrice (symbol: string) =
    async {
        let! response = httpAsyncRequest ("https://api.binance.com/api/v3/ticker/price", [ ("symbol", symbol) ], [], "GET", true)      
        let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        match (response.StatusCode |> LanguagePrimitives.EnumToValue, responseBody) with
        | (200, responseText) ->
            let symbolTicker = BinanceSymbolTickerResponse.Parse responseText
            return (Some symbolTicker)
        | (httpStatus, _) ->
            Log.Warning <| sprintf "Error getting current price - for %s. HTTP %A" symbol httpStatus
            return None
    }

let getOrderBookCurrentPrice (Symbol symbol) =
    async {
        let! response = httpAsyncRequest ("https://api.binance.com/api/v3/ticker/bookTicker", [ ("symbol", symbol) ], [], "GET", true)      
        let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        match (response.StatusCode |> LanguagePrimitives.EnumToValue, responseBody) with
        | (200, responseText) ->
            let symbolTicker = BinanceOrderBookTickerResponse.Parse responseText
            return (Ok {
                OrderBookTickerInfo.Symbol = Symbol symbolTicker.Symbol
                AskPrice = symbolTicker.AskPrice
                BidPrice = symbolTicker.BidPrice
                AskQty = symbolTicker.AskQty
                BidQty = symbolTicker.BidQty
            })
        | (httpStatus, _) ->
            let result = Result.Error <| sprintf "Error getting current order book price - for %s. HTTP %A" symbol httpStatus
            return result
    }

let placeOrder (order: OrderInputInfo) : Async<Result<OrderInfo, OrderError>> = 

    let queryFn (order: OrderInfo) : Async<OrderInfo> =
        async {
            let q = {
                OrderQueryInfo.OrderId = order.OrderId
                Symbol = order.Symbol
            }
            let! queryResult = queryOrderStatus q 
            return {
                order
                with
                    Status = queryResult
                    Time = DateTimeOffset.UtcNow
            }
        }
        
    async {
        Log.Debug ("Starting place order for {Order}", order)

        let orderSide = order.OrderSide
        let (Symbol symbol) = order.Symbol
        let qtyPriceResult = normaliseOrderQuantityAndPrice order.Quantity order.Price orderSide symbol 

        match qtyPriceResult with
        | Ok (normalisedQty, normalisedPrice) -> 
            let! orderResult = placeOrder' order.SignalId orderSide  order.Symbol normalisedQty normalisedPrice
            match orderResult with
            | Ok response ->
                let orderTime = response.TransactTime |> DateTimeOffset.FromUnixTimeMilliseconds
                let order = {
                                OrderInfo.Time = orderTime
                                Quantity = Qty response.OrigQty
                                Price = Price response.Price
                                OrderId = OrderId (string response.OrderId)
                                ClientOrderId = ClientOrderId response.ClientOrderId
                                Symbol = order.Symbol
                                Status = toOrderStatus response.Status (Qty response.ExecutedQty) (Price response.Price)
                                ExecutedQuantity = Qty response.ExecutedQty
                            }

                let! o' = queryFn order

                return (Ok o')

            | Error s -> 
                if s.Contains("400 response") // this is a message we create with that string, when a HTTP 400 happens
                then return (Error <| OrderRejectedError s)
                else return (Error <| OrderError s)
     
        | Error s -> return (Error <| OrderError s)
    }


let ExchangeId = 1L

let fixedFeeRate = 0.1M / 100M // 0.1 %

let getExchange() = {
        new IExchange with
        member __.PlaceOrder o = placeOrder o
        member __.QueryOrder o = queryOrderStatus o
        member __.CancelOrder o = cancelOrder o
        member __.GetOrderBookCurrentPrice s = getOrderBookCurrentPrice s
        member __.Id = Types.ExchangeId ExchangeId
        member __.Name = "Binance"
    }
