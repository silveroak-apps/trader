module Trade.Spot

open System
open Serilog
open FSharp.Control
open Types
open Serilog.Context
open DbTypes

let maxSignalsForCompounding = 5

let private knownExchanges = dict [
    ( Binance.Spot.Trade.ExchangeId, Binance.Spot.Trade.getExchange() )
    ( Simulator.ExchangeId, Simulator.Exchange.get(Binance.Spot.Trade.getExchange()) )
]

let private getExchange (exchangeId: int64) = 
    match knownExchanges.TryGetValue exchangeId with
    | true, exchange -> Some exchange
    | _  -> 
        Log.Error (sprintf "Could not find exchange for exchangeId: %d" exchangeId)
        None

let private getFixedTradeAmount' (exchange: IExchange) (Symbol s) = 
    try
        let getAmtCfg market = 
            let tradeAmountsCfg = appConfig.GetSection("TradeAmounts")
            
            let configForMarket = 
                tradeAmountsCfg.GetChildren()
                |> Seq.find (fun cfg ->  cfg.Key.EndsWith (string market))
            
            configForMarket.Value
            |> Decimal.TryParse
            |> (fun (parsed, v) -> 
                   if parsed && v > 0M then Ok <| v * 1M<qty> 
                   else Error <| sprintf "Could not get config value for market %s - or the value was zero. Symbol: %s" market s)

        getAmtCfg s
    with e ->
        Error (sprintf "Could not getFixedTradeAmount' for symbol %s on exchange %s: %A" s (exchange.GetType().Name) e)

let private updateOrderWith (o: OrderInfo) (statusReason: string) (exo: ExchangeOrder) = 
        let (OrderId oid) = o.OrderId
        { exo with
            Status = string o.Status
            StatusReason = statusReason
            ExchangeOrderId = string oid
            ExchangeOrderIdSecondary = string o.ClientOrderId
            UpdatedTime = DateTime.UtcNow
            ExecutedQty = o.ExecutedQuantity / 1M<qty>
            ExecutedPrice = o.Price / 1M<price> // TODO is o.Price the executed price?
        }

let private cancelIfNotFilled saveOrder (exchange: IExchange) (order: ExchangeOrder) =

    let getExecutedQtyForCancelledOrder orderQuery = 
        async {
            match! (exchange.QueryOrder orderQuery) with
            | OrderCancelled (q, p) -> return (q, p)
            | OrderQueryFailed s ->
                Log.Warning ("Order query failed for order {ExchangeOrderId} (Signal {SignalId}): {QueryFailReason}",
                    orderQuery.OrderId,
                    order.SignalId,
                    s)
                return (Qty 0M, Price 0M)
            | x -> 
                Log.Warning ("Order query returned unexpected status for order {ExchangeOrderId} (Signal {SignalId}): {OrderStatus}",
                    orderQuery.OrderId,
                    order.SignalId,
                    x)
                return (Qty 0M, Price 0M)
        }

    async {
        try
            Log.Debug ("Starting cancel timer for order {Order}, signal {SignalId}", order, order.SignalId)
            let _90Sec = 90 * 1000 // millis
            do! Async.Sleep _90Sec

            let orderQuery = 
                { 
                    OrderQueryInfo.OrderId = OrderId order.ExchangeOrderId
                    Symbol = Symbol order.Symbol
                }
        
            match! (exchange.QueryOrder orderQuery) with
            | OrderFilled (executedQty, executedPrice) -> 
                // save filled status in case we didn't get a socket update
                let updatedOrder = {
                    order with
                        Status = "FILLED"
                        StatusReason = "Order filled"
                        UpdatedTime = DateTime.UtcNow
                        ExecutedQty = executedQty / 1M<qty>
                        ExecutedPrice = executedPrice / 1M<price>
                }
                do! saveOrder updatedOrder |> Async.Ignore

            | _ -> 
                Log.Information("Trying to cancel buy order {ExchangeOrder} (Signal: {SignalId}), because it didn't fill yet.",
                    order,
                    order.SignalId)
                match! (exchange.CancelOrder orderQuery) with
                | Ok true ->
                    // it looks like some websockets like Binance don't seem to be sending an update.
                    // in any case, we better save the updated order here
                    // we need to query again, so we get a potentially updated executedQty
                    Log.Information ("Cancel order ({OrderId}) successful, for signal {SignalId}. Querying latest status...",
                        order.Id,
                        order.SignalId)
                    let! (executedQty, executedPrice) = getExecutedQtyForCancelledOrder orderQuery
                    let updatedOrder = {
                        order with
                            Status = "CANCELED"
                            StatusReason = "Fill timed out."
                            UpdatedTime = DateTime.UtcNow
                            ExecutedQty = executedQty / 1M<qty>
                            ExecutedPrice = executedPrice / 1M<price>
                    }
                    do! saveOrder updatedOrder |> Async.Ignore
                | Ok false ->
                    Log.Warning ("Cancel attempted - but order was not cancelled: {Order}.", order) 
                | Error ex ->
                    Log.Warning (ex, "Could not cancel BUY order {ExchangeOrder} (Signal: {SignalId}). Ignoring...",
                        order, order.SignalId)
        
            // whether it cancelled or not - doesn't matter much. The websocket elsewhere should update the status of the order
            Log.Debug ("Finished cancel timer for order {ExchangeOrder}. (Signal: {SignalId})", order, order.SignalId)
        with e ->
            Log.Warning (e, "Error in cancelIfNotFilled, for order {ExchangeOrder}. (Signal: {SignalId})", order, order.SignalId)
    }

let private getAssetAmount getOrdersForSignal orderSide exchange (signal: Signal) = 
    async {
        if orderSide = OrderSide.BUY then
            (*
            1 BTC = 6000 USD
            Asset currency = BTC
            Market currency = USD
            "Price" of BTC in USD = 6000 ( = Market / Asset)

            If we want to buy BTC worth 200 USD
            MarketAmount = 200 USD
            AssetAmount = (200 / 6000) BTC
            i.e AssetAmount = MarketAmount / Price (of Asset in Market currency)
            *)
            let result = 
                getFixedTradeAmount' exchange (Symbol signal.Symbol)
                |> Result.map (fun marketAmount -> marketAmount / signal.SuggestedPrice)
            return result
        else
            // get total buy order executed qty for this signal
            let! orders = getOrdersForSignal signal.SignalId
            let totalBoughtAmount = 
                Seq.sumBy (fun o -> o.ExecutedQty - o.FeeAmount) (orders
                |> Seq.filter (fun o -> String.Equals(o.OrderSide, "BUY", StringComparison.OrdinalIgnoreCase) && o.ExecutedQty > 0M))
        
            return (if totalBoughtAmount > 0M 
                    then Ok (totalBoughtAmount * 1M<qty>) 
                    else 
                        Log.Warning ("Got BUY orders: {Orders}, no bought quantity when trying to sell.", orders)
                        Error <| sprintf "No bought quantity for signal %d" signal.SignalId)
    }

let private placeOrder getOrdersForSignal (saveOrder: ExchangeOrder -> Async<Result<int64, exn>>) (s: Signal) (placeRealOrders: bool) =
    // TODO - this needs a lot of cleanup and unification with the Futures trade flow

    async {
        // don't pushproperty outside this scope - so the recursive message loop gets it. We dont want that!
        use _ = LogContext.PushProperty("SignalId", s.SignalId)
        use _ = LogContext.PushProperty("ExchangeId", s.ExchangeId)

        let exchangeId = if placeRealOrders then s.ExchangeId else Simulator.ExchangeId
        match getExchange exchangeId with
        | None -> ()
        | Some exchange ->
            Log.Information ("Placing an order for signal {Signal} using exchange {Exchange}", s, exchange.GetType().Name)
            let exOrderAsyncResult = 
                let sym = s.Symbol |> Symbol

                let orderSide =
                    if s.SignalType = "BUY" then OrderSide.BUY else OrderSide.SELL

                let assetAmountResult = getAssetAmount getOrdersForSignal orderSide exchange s

                async {
                    match! assetAmountResult with
                    | Error e -> return (Error e)
                    | Ok assetQty  -> 

                        match! exchange.GetOrderBookCurrentPrice s.Symbol with
                        | Error msg -> return (Error <| sprintf "Could not get latest orderbook price before placing order for %s: %s" s.Symbol msg)
                        | Ok price ->
                            // intentionally reduce potential loss due to spread
                            let orderPrice =
                                // this works when we go long
                                // for short, we need to reverse the logic and play safe for buy
                                if orderSide = BUY then Math.Min(s.SuggestedPrice, price.BidPrice)
                                else
                                    // for sell check if the current 'best ask' is better than what we thought
                                    // or if it is worse, it is not too bad < 0.1% diff
                                    if price.AskPrice - s.SuggestedPrice > 0M || (s.SuggestedPrice - price.AskPrice) * 100M / price.AskPrice < 0.1M then
                                        price.AskPrice
                                    else s.SuggestedPrice
                            Log.Information("Placing {OrderSide} order with reduced spread loss. Signal suggested price: {SignalSuggestedPrice}. Order request price: {OrderRequestPrice}",
                                orderSide,
                                s.SuggestedPrice,
                                orderPrice
                                )
                            let orderInput = {
                                OrderInputInfo.SignalId = s.SignalId
                                OrderSide = orderSide
                                Price = orderPrice * 1M<price>
                                Symbol = sym
                                Quantity = assetQty
                                PositionSide = NOT_APPLICABLE
                                OrderType = OrderType.LIMIT
                            }

                            // we're about to place an order: record it in the db, so we have the link between the order and signal saved
                            let exo =
                                    {
                                        ExchangeOrder.CreatedTime = DateTime.UtcNow
                                        Id = 0L // to be assigned by the db
                                        Status = "READY"
                                        StatusReason = "About to place Order"
                                        Symbol = string orderInput.Symbol
                                        Price = orderInput.Price / 1M<price>
                                        OriginalQty = orderInput.Quantity / 1M<qty>
                                        ExchangeOrderIdSecondary = string orderInput.SignalId
                                        SignalId = s.SignalId
                                        SignalCommandId = 0L
                                        UpdatedTime = DateTime.UtcNow
                                        ExchangeId = s.ExchangeId
                                        OrderSide = string orderInput.OrderSide
                                        LastTradeId = 0L
                                        ExchangeOrderId = ""
                                        ExecutedPrice = 0M // the order is not yet executed - this should be updated by trade status updates
                                        ExecutedQty = 0M
                                        FeeAmount = 0M // initially we've not executed anything - so no fees yet
                                        FeeCurrency = "" // nothing is executed yet. so we dont know what this is.
                                    }

                            let! exoIdResult = saveOrder exo
                            match exoIdResult with
                            | Error err -> 
                                Log.Error ("Error saving newly placed spot order: {Error}", err)
                                return (Error <| string err)
                            | Ok exoId ->
                                Log.Information ("Saved order {ExchangeOrderId} as READY for signal {SignalId}", exoId, s.SignalId)

                                match! exchange.PlaceOrder orderInput with
                                | Ok o ->
                                
                                    let exo' =
                                        { exo with Id = exoId }
                                        |> updateOrderWith o "Placed order"

                                    // save to db here to record it in case there's some problem with the websocket updates
                                    // atleast we'll have a reference to call the Exchange later
                                    // If the websocket updates work, and come in before this save happens,
                                    // it should still work because the websocket update will find it by id / signal id + order side
                                    do! saveOrder exo' |> Async.Ignore
                                    Log.Information ("Saved order {ExchangeOrderId} as {Status} for signal {SignalId}",
                                        exoId,
                                        exo'.Status,
                                        s.SignalId)
                                    return (Ok exo')

                                | Error (OrderRejectedError ore) ->
                            
                                    do! saveOrder { exo with Id = exoId; Status = "REJECTED"; StatusReason = ore } |> Async.Ignore
                                    return (Error ore)

                                | Error (OrderError oe) ->
                                
                                    do! saveOrder { exo with Id = exoId; Status = "ERROR"; StatusReason = oe } |> Async.Ignore
                                    return (Error oe)
                }

            match! exOrderAsyncResult with
            // Removed the cancelation as - during the bull run it may not be required (2021 - Jan)
            | Ok _exOrder ->
                ()
                // start a thread that cancels the order if not filled
                // if placeRealOrders then
                //     cancelIfNotFilled exchange exOrder |> Async.Start
            | Error s -> 
                Log.Error ("Error placing order {OrderError}", s)
    }
type private TradeAgentCommand = 
    | Trade of Signal * bool * AsyncReplyChannel<unit>

// serialising calls to placeOrder to be safe
let private mkTradeAgent getOrdersForSignal saveOrder =
    // safeguard against placing an order for the same signal twice - during the app's lifetime:
    let signalsProcessed = System.Collections.Generic.Dictionary<string, DateTimeOffset>()

    // only keep recent signals
    let cleanupSignalsInMemory () =
        if signalsProcessed.Count > 1000 then
            let now = DateTimeOffset.UtcNow
            let keysToRemove =
                signalsProcessed
                |> Seq.filter (fun kv -> (now - kv.Value).TotalMinutes > 30.0)
                |> Seq.map (fun kv -> kv.Key)
                |> Seq.toList
            keysToRemove |> Seq.iter (signalsProcessed.Remove >> ignore)

    let agent = 
        MailboxProcessor<TradeAgentCommand>.Start (fun inbox ->
            let rec messageLoop() = async {
                let! (Trade (s, placeRealOrders, replyCh)) = inbox.Receive()

                try
                    let key = sprintf "%s-%d" s.SignalType s.SignalId
                    if not <| signalsProcessed.ContainsKey key then
                        signalsProcessed.[key] <- DateTimeOffset.UtcNow
                        do! placeOrder getOrdersForSignal saveOrder s placeRealOrders
                    else
                        Log.Warning ("{SignalType} Order for signal {SignalId} has already been placed. Skipping", s.SignalType, s.SignalId)
                    replyCh.Reply()
                with e ->
                    Log.Error (e, "Error handling trade command: {TradeCommand} for signal {SignalId}", s, s.SignalId)

                cleanupSignalsInMemory ()
                return! messageLoop()
            }
            messageLoop()
        )
    // need to add an error handler to ensure the process crashes properly
    // we might later need to make this smarter, to crash only on repeated exceptions of the same kind or 
    // something where the exception is unrecoverable
    agent.Error.Add(raise)
    agent

let mutable private tradeAgent: MailboxProcessor<TradeAgentCommand> option = None

let processValidSignals getSignalsToBuyOrSell expireSignals getOrdersForSignal getExchangeOrder saveOrder (placeRealOrders: bool) =
    let saveOrder' exo = saveOrder exo SPOT

    async {
        if placeRealOrders then Log.Debug ("Getting signals to buy/sell")
        let! signals = getSignalsToBuyOrSell ()

        let numSignals = signals |> Seq.length
        if numSignals > 0
        then Log.Information ("Got {TradeSignalCount} signals to buy/sell", numSignals)

        // in fake / simulation mode, don't expire any signals
        let oldBuySignals =
            if (placeRealOrders) then
                signals |> Seq.filter (fun s -> (DateTime.UtcNow - s.SignalDateTime).TotalMinutes > 2.0 && s.SignalType = "BUY") // anything older than a couple of minutes
            else Seq.empty

        let validSignals = signals |> Seq.except oldBuySignals

        // ugly mutable for now :/
        if tradeAgent = None then
            tradeAgent <- Some <| mkTradeAgent getOrdersForSignal saveOrder'

        do! 
            validSignals
            |> Seq.distinctBy (fun s -> sprintf "%s-%s" s.Symbol s.SignalType) // we don't want to buy or sell the same symbol twice (this is one level of protection)
            |> AsyncSeq.ofSeq
            |> AsyncSeq.iterAsync (
                fun s -> 
                    match tradeAgent with
                    | Some agent -> agent.PostAndAsyncReply (fun replyCh -> Trade (s, placeRealOrders, replyCh))
                    | _ -> raise <| exn "Unexpected error: trade agent is not setup"
                )
        
        let lapsedStats = oldBuySignals |> Seq.map(fun s -> s.SignalId, DateTime.UtcNow, s.SignalDateTime, (DateTime.UtcNow - s.SignalDateTime).TotalMinutes)
        if not (oldBuySignals |> Seq.isEmpty) then
            Log.Debug ("Found {OldSignalCountThisTick} old signals. Setting to lapsed: {OldBuySignals}. {SignalLapsedStats}", oldBuySignals, lapsedStats)
            do! expireSignals oldBuySignals

    }
