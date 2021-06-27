module Trade.Futures

open System
open System.Collections.Generic
open Serilog
open Serilog.Context

open FSharpx.Control
open FSharp.Control
open FsToolkit.ErrorHandling

open DbTypes
open Types
open Trader.Exchanges

let private findOrderSide (positionSide: PositionSide) (signalAction: SignalAction) = 
    match positionSide, signalAction with
    | LONG, OPEN      -> BUY  |> Ok
    | LONG, INCREASE  -> BUY  |> Ok
    | LONG, DECREASE  -> SELL |> Ok
    | LONG, CLOSE     -> SELL |> Ok

    | SHORT, OPEN     -> SELL |> Ok
    | SHORT, INCREASE -> SELL |> Ok
    | SHORT, DECREASE -> BUY  |> Ok
    | SHORT, CLOSE    -> BUY  |> Ok

    | _, _            -> Result.Error <| exn (sprintf "Unsupported positionType: %A, signalAction: %A" positionSide signalAction)

let determineOrderBookPrice' (exchange: IExchange) (symbol: Symbol) (positionSide: PositionSide) (orderSide: OrderSide) = 
    asyncResult {
        let! orderBook = exchange.GetOrderBookCurrentPrice symbol
        // reduce potential loss due to spread
        let result =
            match orderSide, positionSide with
            | BUY,  LONG  -> 
                // let diff = orderBook.BidPrice - s.Price
                orderBook.BidPrice
            | BUY,  SHORT -> orderBook.BidPrice
            | SELL, LONG  -> orderBook.AskPrice
            | SELL, SHORT -> orderBook.AskPrice
            | _           -> raise (exn <| sprintf "Unexpected order side / position side values: '%A, %A' trying to determine order price" orderSide positionSide)
        return result
    }
    |> AsyncResult.mapError exn
    |> AsyncResult.catch id

type private OrderPriceAndSlippage = {
    OrderBookPrice: decimal
    SlippageFromCommand: decimal
}

let calcSlippage (orderSide: OrderSide) orderBookPrice desiredPrice =
    match orderSide with
    | BUY  -> ( orderBookPrice - desiredPrice ) * 100M / desiredPrice
    | SELL -> ( desiredPrice - orderBookPrice ) * 100M / desiredPrice
    | _    ->  0M // not relevant won't happen :/

let private determineOrderBookPrice (exchange: IExchange) (s: FuturesSignalCommandView) =
    asyncResult {
        let positionSide = PositionSide.FromString s.PositionType
        let! orderSide = findOrderSide positionSide (SignalAction.FromString s.Action)
        let! price = determineOrderBookPrice' exchange (Symbol s.Symbol) positionSide orderSide
        let slippage = calcSlippage orderSide price s.Price
        return {
            OrderBookPrice = price
            SlippageFromCommand = slippage
        }
    }

let toExchangeOrder (signalCommand: FuturesSignalCommandView) (o: OrderInfo) = 
    let orderSide = findOrderSide (PositionSide.FromString signalCommand.PositionType) (SignalAction.FromString signalCommand.Action)
    let (OrderId oid) = o.OrderId
    Result.map (fun os ->
            {
                ExchangeOrder.CreatedTime = DateTime.UtcNow
                Id = 0L // to be assigned by the data store
                Status = string o.Status
                StatusReason = "Placed Order"
                Symbol = string signalCommand.Symbol
                Price =  o.Price / 1M<price>
                OriginalQty = signalCommand.Quantity
                ExchangeOrderIdSecondary = string o.ClientOrderId
                SignalId = signalCommand.SignalId
                SignalCommandId = signalCommand.Id
                UpdatedTime = DateTime.UtcNow
                ExchangeId = signalCommand.ExchangeId
                OrderSide = string os
                LastTradeId = 0L
                ExchangeOrderId = string oid
                ExecutedPrice = o.Price / 1M<price> 
                ExecutedQty = o.ExecutedQuantity / 1M<qty>
                FeeAmount = 0M // initially we've not executed anything - so no fees yet
                FeeCurrency = "" // nothing is executed yet. so we dont know what this is.
            }
        ) orderSide

let placeOrder (exchange: IExchange) (s: FuturesSignalCommandView) (bestOrderPrice: decimal) =
    asyncResult {
        Log.Information ("Placing an order for command {Command} for signal {SignalId} using exchange {Exchange}", s, s.SignalId, exchange.GetType().Name)

        let sym = s.Symbol |> Symbol
        let! orderSide = findOrderSide (PositionSide.FromString s.PositionType) (SignalAction.FromString s.Action)
        let assetQty = Math.Abs(s.Quantity) * 1M<qty> // need to take abs value to ensure the right amount is used for longs/shorts

        Log.Information("Placing {OrderSide} order with reduced spread loss. Signal suggested price: {SignalSuggestedPrice}. Order request price: {OrderRequestPrice}",
            orderSide,
            s.Price,
            bestOrderPrice)

        let orderInput = {
            OrderInputInfo.SignalId = s.SignalId
            OrderSide = orderSide
            Price = bestOrderPrice * 1M<price>
            Symbol = sym
            Quantity = assetQty
            PositionSide = PositionSide.FromString s.PositionType
            OrderType = OrderType.LIMIT
            SignalCommandId = s.Id
        }

        // TODO: Review this flow and see if we need to indicate that a 'rejected' order can't be simply retried
        let mapOrderError err =
            match err with
            | OrderRejectedError ore ->
                Log.Error("Error (order rejected by exchange) trying to execute signal command {SignalCommandId}: {Error}", s.Id, ore)
                ore
            | OrderError oe ->
                Log.Error("Error trying to execute signal command {SignalCommandId}: {Error}", s.Id, oe)
                oe
            |> exn

        let! o = exchange.PlaceOrder orderInput |> AsyncResult.mapError mapOrderError

        let! exo = 
            toExchangeOrder s o

        return exo
    } |> AsyncResult.catch id

let private retryCount = 10
let private delay = TimeSpan.FromSeconds(1.0)

// TODO: we need to start identifying what sort of things can be retried, and what can't
// eg. rejected orders won't work unless the inputs are changed

let placeOrderWithRetryOnError (exchange: IExchange) (signalCmd: FuturesSignalCommandView) (bestOrderPrice: decimal) =
    // retry 'retryCount' times, in quick succession if there is an error placing an order
    let placeOrder' () = placeOrder exchange signalCmd bestOrderPrice
    placeOrder' |> withRetryOnErrorResult retryCount delay "placeOrderWithRetryOnError" () isAlwaysRetryable

let queryOrderWithRetryOnError (exchange: IExchange) (orderQuery: OrderQueryInfo) = 
    let queryOrder () =
        async {
            try
                let! orderStatus = exchange.QueryOrder orderQuery
                return Result.Ok orderStatus
            with
            | e -> return (Result.Error e)
        }
    queryOrder |> withRetryOnErrorResult retryCount delay "queryOrderWithRetryOnError" () isAlwaysRetryable

let cancelOrderWithRetryOnError (exchange: IExchange) (orderQuery: OrderQueryInfo) =
    let cancelOrder () =
        exchange.CancelOrder orderQuery
        |> AsyncResult.mapError exn
        |> AsyncResult.catch id
    cancelOrder |> withRetryOnErrorResult retryCount delay "cancelOrderWithRetryOnError" () isAlwaysRetryable

let private getLatestOrderState exchange order =
    let waitMillis = 500.0
    let maxAttempts = 20 // with exponential backoff, this will take us upto a wait of 6 days!
    let attempt = 1
    
    let rec getLatestOrderStateWithBackOff waitMillis attempt exchange order orderQuery =
        asyncResult {
            let! orderStatus = queryOrderWithRetryOnError exchange orderQuery
            match orderStatus with
            | OrderFilled (executedQty, executedPrice) ->
                return {
                    order with
                        Status = string orderStatus
                        StatusReason = "Order filled"
                        UpdatedTime = DateTime.UtcNow
                        ExecutedQty = executedQty / 1M<qty>
                        ExecutedPrice = executedPrice / 1M<price>
                }
            | OrderPartiallyFilled (qty, price) ->
                return {
                    order with
                        Status = string orderStatus
                        StatusReason = "Partially filled"
                        ExecutedQty = qty / 1M<qty>
                        ExecutedPrice = price / 1M<price>
                }
            | OrderCancelled (qty, price) ->
                return {
                    order with
                        Status = string orderStatus
                        StatusReason = "Fill timed out"
                        ExecutedQty = qty / 1M<qty>
                        ExecutedPrice = price / 1M<price>
                }
            | OrderQueryFailed s ->
                Log.Warning ("Order query failed: {Error} Trying again...", s)
                if attempt > maxAttempts
                then return order
                else 
                    do! Async.Sleep (TimeSpan.FromMilliseconds(waitMillis))
                    let waitMillis' = waitMillis * Math.Pow(2.0, float attempt)
                    return! getLatestOrderStateWithBackOff waitMillis' (attempt + 1) exchange order orderQuery
            | _ -> return order
        }

    let orderQuery = {
        OrderQueryInfo.OrderId = OrderId order.ExchangeOrderId
        Symbol = Symbol order.Symbol
    }
    Log.Information("Querying order status after waiting... {Query}", orderQuery)

    getLatestOrderStateWithBackOff waitMillis attempt exchange order orderQuery

let private getFilledQty (orders: ExchangeOrder seq) = 
        if Seq.isEmpty orders
        then 0M
        else orders |> Seq.sumBy (fun o -> o.ExecutedQty)
  
let private getUpdatedCommandWithRemainingQty 
    (getPositionSizeFromDataStore: SignalId -> Async<Result<decimal, exn>>)
    (ordersSoFar: ExchangeOrder list)
    (signalCommand: FuturesSignalCommandView) =

    asyncResult {
        let! openedPositionSize = getPositionSizeFromDataStore (SignalId signalCommand.SignalId)
        let cmd = 
            match signalCommand.Action with
            | "CLOSE" ->
                let newCmd = 
                    { signalCommand with Quantity = openedPositionSize } // this will always tell us how much position we have left open
                Result.Ok newCmd
            | "OPEN" ->
                // check what we attempted in the previous attempts
                let executedQtySoFar = getFilledQty ordersSoFar
                let newCmd = 
                    { signalCommand with Quantity = signalCommand.Quantity - executedQtySoFar }
                Ok newCmd
            | x -> Result.Error (exn <| (sprintf "Unknown signal command action: %s" x)) // TODO model this better
        return! cmd
    }

type private TradeFlowWaitResult =
| MaxWaitTimeReached of ExchangeOrder
| PriceMoved of ExchangeOrder
| OrderFilled of ExchangeOrder

let rec private waitForPriceMovementOrMaxTime (waitInterval: TimeSpan) (maxRetryTime: TimeSpan) (exchange: IExchange) (signalCommand: FuturesSignalCommandView) (order: ExchangeOrder) =
    asyncResult {

        // wait for a bit and check price and order status
        do! Async.Sleep (int waitInterval.TotalMilliseconds)

        let! updatedOrder = getLatestOrderState exchange order
        if updatedOrder.Status = "FILLED"
        then
            return OrderFilled updatedOrder
        else
            // find best price for our order and see if it has changed.
            // if it hasn't keep waiting till max retry time       
            let referencePrice = order.Price
            let! ops = determineOrderBookPrice exchange signalCommand
            
            let positionSide = PositionSide.FromString signalCommand.PositionType
            let signalAction = SignalAction.FromString signalCommand.Action
            let! orderSide = findOrderSide positionSide signalAction

            let priceChangedUnfavourably = 
                match orderSide with
                | BUY -> ops.OrderBookPrice > referencePrice
                | SELL -> ops.OrderBookPrice < referencePrice
                | _ -> false
            
            Log.Debug("DEBUG: Order Book Price {OrderBookPrice}, Reference (Order) Price {ReferencePrice}",ops.OrderBookPrice, referencePrice)
            if not priceChangedUnfavourably && (DateTime.UtcNow - order.CreatedTime) < maxRetryTime
            then
                
                return! (waitForPriceMovementOrMaxTime waitInterval maxRetryTime exchange signalCommand order)
            else if priceChangedUnfavourably
            then
                return PriceMoved updatedOrder
            else
                return MaxWaitTimeReached updatedOrder
    }

let private any = Seq.exists id

let rec executeOrdersForCommand
    (exchange: IExchange)
    (saveOrder: ExchangeOrder -> Async<Result<ExchangeOrder, exn>>)
    (getPositionSizeFromDataStore: SignalId -> Async<Result<decimal, exn>>)
    (maxSlippage: decimal)
    (ordersSoFar: ExchangeOrder list)
    (cancellationDelaySeconds: int)
    (maxWaitTime: TimeSpan)
    (maxAttempts: int) 
    (attempt: int)
    (signalCommand: FuturesSignalCommandView) =

    asyncResult {

        // // don't pushproperty outside this scope - so the recursive message loop gets it. We dont want that!
        // use _ = LogContext.PushProperty("SignalCommandId", signalCommand.Id)
        // use _ = LogContext.PushProperty("SignalId", signalCommand.SignalId)
        // use _ = LogContext.PushProperty("Exchange", exchange.GetType().Name)
        (*
           Exit when
            DONE - max attempts reached
            DONE - max wait time for the order reached
            DONE - order filled
            DONE - max slippage reached

           Waiting for order fill
            DONE - price check from order book
            DONE - check order status
            - wait
                DONE - exit when order filled 
                DONE - price moved
                DONE - max wait time for max time reached
            - Count as new attempt, if order is not filled
        *)

        // first, we need to find out how much more qty to place an order for:
        let maybeLatestOrder = ordersSoFar |> List.tryHead
        let! commandWithRemainingQty = getUpdatedCommandWithRemainingQty getPositionSizeFromDataStore ordersSoFar signalCommand
        let! ops = determineOrderBookPrice exchange signalCommand
        
        // exit conditions
        let slippageCrossed = ops.SlippageFromCommand > maxSlippage && signalCommand.Action = "OPEN"
        let maxAttemptsCompleted = attempt > maxAttempts
        let isCommandFilled = commandWithRemainingQty.Quantity = 0M // let's assume that the latest order was queried and saved to the db previously so this number from the db is accurate
        let maxWaitTimeReached =
            match maybeLatestOrder with
            | Some latestOrder -> (DateTime.UtcNow - latestOrder.CreatedTime) > maxWaitTime
            | None -> false

        let exitConditions = [
            slippageCrossed
            maxAttemptsCompleted
            maxWaitTimeReached
            isCommandFilled
        ]

        if any exitConditions
        then 
            Log.Information("Done executing command {SignalCommandId} {successPhrase}. Retried {MaxAttempts} times with {OrderCount} orders. Finishing signal command {originalSignalCommand}...",
                signalCommand.Id,
                (if isCommandFilled then "successfully" else "partially"),
                maxAttempts,
                ordersSoFar.Length,
                signalCommand)
            
            if slippageCrossed
            then Log.Warning("Not Placing any further orders for command {Command}. Signal suggested price: {SignalSuggestedPrice}. Order request price: {OrderRequestPrice} as slippage crossed {Slippage}",
                    signalCommand,
                    signalCommand.Price,
                    ops.OrderBookPrice,
                    ops.SlippageFromCommand)

            if maxAttemptsCompleted
            then Log.Warning("Not Placing any further orders for command {Command}. Max attempts ({MaxAttempts}) completed.", signalCommand, maxAttempts)

            if maxWaitTimeReached
            then Log.Warning("Not Placing any further orders for command {Command}. Max wait time reached ({MaxAttempts}).", signalCommand, maxWaitTime)

            return ordersSoFar
        else
            Log.Information ("Starting executeOrder attempt: {Attempt}/{MaxAttempts} for command {Command}",  attempt, maxAttempts, signalCommand)

            let! exchangeOrder = placeOrderWithRetryOnError exchange commandWithRemainingQty ops.OrderBookPrice
            let! newOrder = saveOrder exchangeOrder

            Log.Information("Saved new order: {InternalOrderId}. {Order}", newOrder.Id, newOrder)
            Log.Information("Waiting atleast {CancellationDelaySeconds} seconds before querying and cancelling if needed.", cancellationDelaySeconds)

            let! waitResult = 
                waitForPriceMovementOrMaxTime 
                    (TimeSpan.FromSeconds <| float cancellationDelaySeconds)
                    maxWaitTime 
                    exchange
                    signalCommand
                    newOrder

            match waitResult with
            | OrderFilled updatedOrder -> 
                let! filledOrder' = saveOrder updatedOrder
                Log.Information("Filled order: {ExchangeOrder}", filledOrder')
                return (filledOrder' :: ordersSoFar)

            | MaxWaitTimeReached updatedOrder ->
                let! updatedOrder' = saveOrder updatedOrder
                Log.Warning("Not Placing any further orders for command {Command}. Max wait time reached ({MaxAttempts}).", signalCommand, maxWaitTime)
                return (updatedOrder' :: ordersSoFar)

            | PriceMoved updatedOrder ->
                let! updatedOrder' = saveOrder updatedOrder
                Log.Information("Trying to cancel order {ExchangeOrder} (Signal: {SignalId}), because it didn't fill yet. Current status: {OrderStatus}",
                    updatedOrder',
                    updatedOrder'.SignalId,
                    updatedOrder'.Status)

                let! cancelled = 
                    let orderQuery = {
                        OrderQueryInfo.OrderId = OrderId newOrder.ExchangeOrderId
                        Symbol = Symbol newOrder.Symbol
                    }
                    cancelOrderWithRetryOnError exchange orderQuery

                // we need to query again, so we get a potentially updated executedQty
                Log.Information ("Cancel order ({OrderId}) attempted, for signal {SignalId}, command {SignalCommandId}. Success: {Success}. Querying latest status...",
                    newOrder.Id, newOrder.SignalId, signalCommand.Id, cancelled)

                let! updatedOrder' = getLatestOrderState exchange updatedOrder 
                let! updatedOrder'' = saveOrder updatedOrder'

                Log.Debug ("Saved order after cancellation attempt: {UpdatedOrder}", updatedOrder'')

                let updatedOrders = updatedOrder'' :: ordersSoFar

                let! orders = 
                    executeOrdersForCommand 
                        exchange 
                        saveOrder 
                        getPositionSizeFromDataStore
                        maxSlippage
                        updatedOrders
                        cancellationDelaySeconds
                        maxWaitTime
                        maxAttempts (attempt + 1)
                        signalCommand // send the original command for logging - we always figure out what the right qty remaining is.

                return orders
    }

type private TradeAgentCommand = 
    | FuturesTrade of FuturesSignalCommandView * bool * AsyncReplyChannel<unit>

// serialising calls to placeOrder to be safe
let private mkTradeAgent 
    (saveOrder: ExchangeOrder -> Async<Result<ExchangeOrder, exn>>)
    (getPositionSize: SignalId -> Async<Result<decimal, exn>>)
    (completeSignalCommands: SignalCommandId seq -> SignalCommandStatus -> Async<Result<unit, exn>>) =
    // safeguard against placing an order for the same signal twice - during the app's lifetime:
    let commandsProcessed = Dictionary<int64, DateTimeOffset>()

    // only keep recent signals
    let cleanupSignalsInMemory () =
        if commandsProcessed.Count > 1000 then
            let now = DateTimeOffset.UtcNow
            let keysToRemove =
                commandsProcessed
                |> Seq.filter (fun kv -> (now - kv.Value).TotalMinutes > 30.0)
                |> Seq.map (fun kv -> kv.Key)
                |> Seq.toList
            keysToRemove |> Seq.iter (commandsProcessed.Remove >> ignore)

    let agent = 
        MailboxProcessor<TradeAgentCommand>.Start (fun inbox ->
            let rec messageLoop() = async {
                let! (FuturesTrade (s, placeRealOrders, replyCh)) = inbox.Receive()
                use _ = LogContext.PushProperty("SignalId", s.SignalId)
                use _ = LogContext.PushProperty("SignalCommandId", s.Id)

                try
                    let key = s.Id
                    if not <| commandsProcessed.ContainsKey key then
                        commandsProcessed.[key] <- DateTimeOffset.UtcNow

                        let exchangeId = if placeRealOrders then s.ExchangeId else Simulator.ExchangeId
                        let attemptCount = 1
                        let maxAttempts = if s.Action = "OPEN" then 10 else 100 //TODO: move to config
                        let cancellationDelay = if s.Action = "OPEN" then 5 else 5 // seconds
                        let maxSlippage = Strategies.Common.tradePriceSlippageAllowance
                        let totalWaitTime = TimeSpan.FromMinutes 3.0 // per order, we wait a total of this amount of time (including all retries) //TODO: move to config

                        (*
                        1 find our which exchange to place order
                        2 find latest orderbook price from exchange (handle errors + 3 retries to get latest price) 
                        3 place order (handle network errors, Exchange API errors etc and retry upto 3 times OR less -> till we get an orderID back)
                        4 save order (handle errors + 3 retries for db errors)
                        5 wait for order to fill for 'x' seconds
                        6 query order (handle errors + 3 retries) 
                        6 if filled, save order (handle errors + 3 retries for db errors) --> done
                        7 if not filled, cancel order (handle errors + 3 retries for network/Exchange errors)
                        8 if not filled, repeat steps from 2 -> with a new order, and updated qty (only use remaining qty) : repeat 'n' times for OPEN, 'm' times for CLOSE
                        9 if retries at any stage are exhausted, give up
                        10 if we manage to fill order fully, saveOrder (the new ones that happen during retries + handle db save errors)
                        
                        *)

                        asyncResult {
                            let! exchange = (lookupExchange (ExchangeId exchangeId) |> Result.mapError exn)
                            let! orders = executeOrdersForCommand exchange saveOrder getPositionSize maxSlippage [] cancellationDelay totalWaitTime maxAttempts attemptCount s
                            let executedQty = getFilledQty orders
                            let signalCommandStatus =
                                if executedQty > 0M
                                then SignalCommandStatus.SUCCESS
                                else SignalCommandStatus.FAILED
                            do! completeSignalCommands [(SignalCommandId s.Id)] signalCommandStatus
                        }
                        |> AsyncResult.mapError (fun ex -> 
                                async {
                                    Log.Error(ex, "Error processing command: {Command} for signal: {SignalId}", s, s.SignalId)
                                    let! result = completeSignalCommands [(SignalCommandId s.Id)] SignalCommandStatus.FAILED
                                    match result with
                                    | Result.Error ex' -> 
                                        Log.Error (ex', "Error marking command: {SignalCommandId} as failed for signal: {SignalId}", s.Id, s.SignalId)
                                    | _ -> ()
                                } 
                                |> Async.Ignore
                                |> Async.Start
                            )
                        |> Async.Ignore
                        |> Async.Start
                        
                    else
                        Log.Warning ("Command {SignalCommandId} for signal {SignalId} has already been processed. Skipping ...", s.Id, s.SignalId)

                    replyCh.Reply()
                with e ->
                    Log.Error (e, "Error handling trade msg: for SignalCommand {SignalCommandId} {SignalId}", s, s.SignalId)

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

let processValidSignals
    (getFuturesSignalCommands: unit -> Async<Result<FuturesSignalCommandView seq, exn>>)
    (completeSignalCommands: SignalCommandId seq -> SignalCommandStatus -> Async<Result<unit, exn>>)
    (getExchangeOrder: int64 -> Async<Result<ExchangeOrder option, exn>>)
    (saveOrder: ExchangeOrder -> TradeMode -> Async<Result<int64, exn>>)
    (getPositionSize: SignalId -> Async<Result<decimal, exn>>)
    (placeRealOrders: bool) =

    let saveOrder' exo = 
        async {
            let! exoIdResult = saveOrder exo FUTURES
            return Result.map (fun exoId -> { exo with ExchangeOrder.Id = exoId }) exoIdResult
        }
    
    asyncResult {
        Log.Debug ("Getting signal commands to action")
        let! signalCommands = getFuturesSignalCommands ()
        
        let countOfCommands = signalCommands |> Seq.length
        if countOfCommands > 0
        then Log.Information ("Got {TradeSignalCount} commands to action", countOfCommands)

        let latestCommands = 
            signalCommands
            |> Seq.sortByDescending (fun s -> s.RequestDateTime) // we want the latest command for each signal
            |> Seq.distinctBy (fun s -> s.SignalId)  // this discards everything but the first occurrence in the sequence (which happens to be the latest due to the descending sort)

        let oldCommands =
            latestCommands 
            |> Seq.filter (fun s -> (DateTime.UtcNow - s.RequestDateTime).TotalSeconds > 100.0) // anything older than 10 seconds (futures moves fast)
            
        let validCommands = latestCommands |> Seq.except oldCommands

        // ugly mutable for now :/
        if tradeAgent = None then
            tradeAgent <- Some <| mkTradeAgent saveOrder' getPositionSize completeSignalCommands

        do! 
            validCommands
            |> AsyncSeq.ofSeq
            |> AsyncSeq.iterAsyncParallel (
                fun s -> 
                    match tradeAgent with
                    | Some agent -> agent.PostAndAsyncReply (fun replyCh -> FuturesTrade (s, placeRealOrders, replyCh))
                    | _ -> raise <| exn "Unexpected error: trade agent is not setup processing {SignalId}"
                )

        // now we need to 'expire' / invalidate commands that won't be run
        // there are two categories of invalid commands:
        // 1. Too old
        // 2. A newer command is available

        // category 1: too old / stale
        let lapsedStats = oldCommands |> Seq.map(fun s -> s.SignalId, DateTime.UtcNow, s.RequestDateTime, (DateTime.UtcNow - s.RequestDateTime).TotalSeconds)
        if not (oldCommands |> Seq.isEmpty) then
            Log.Debug ("Found {SignalCountThisTick} old signals. Lapsted stats: {SignalLapsedStats}. Setting to lapsed: {SignalCommands}",
                 oldCommands |> Seq.length,
                 lapsedStats,
                 oldCommands)
            let cmdIds = 
                oldCommands 
                |> Seq.map(fun s -> SignalCommandId s.Id)
            cmdIds |> Seq.map (fun c -> Log.Warning("Trying to expire {SignalCommandId} for the {SignalId}", c, SignalId)) |> ignore

            do! completeSignalCommands cmdIds SignalCommandStatus.EXPIRED

        // category 2: newer command is available, so these previousCommands are invalid now
        let previousCommands = signalCommands |> Seq.except latestCommands
        let lapsedStats = previousCommands |> Seq.map(fun s -> s.SignalId, DateTime.UtcNow, s.RequestDateTime, (DateTime.UtcNow - s.RequestDateTime).TotalSeconds)
        if not (previousCommands |> Seq.isEmpty) then
            Log.Debug ("For {SignalId} Found {SignalCountThisTick} overridden signals. Lapsed stats: {SignalLapsedStats}. Setting to lapsed: {SignalCommands}.",
                SignalId,
                previousCommands |> Seq.length, 
                lapsedStats,
                previousCommands)
            let cmdIds = previousCommands |> Seq.map(fun s -> SignalCommandId s.Id)
            do! completeSignalCommands cmdIds SignalCommandStatus.EXPIRED
    }
    |> AsyncResult.mapError (fun ex -> Log.Error(ex, "Error processing signal commands"))
    |> Async.Ignore
