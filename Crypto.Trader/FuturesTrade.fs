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

let private updateOrderWith (o: OrderInfo) (statusReason: string) (exo: ExchangeOrder) = 
        let (OrderId oid) = o.OrderId
        { exo with
            Status = string o.Status
            StatusReason = statusReason
            ExchangeOrderId = string oid
            ExchangeOrderIdSecondary = string o.ClientOrderId
            UpdatedTime = DateTime.UtcNow
            ExecutedQty = o.ExecutedQuantity / 1M<qty>
            ExecutedPrice = o.Price / 1M<price>
        }

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

let determineOrderPrice (exchange: IExchange) (s: FuturesSignalCommandView) (orderSide: OrderSide) = 
    asyncResult {
        let! orderBook = exchange.GetOrderBookCurrentPrice s.Symbol
        // reduce potential loss due to spread
        let positionSide = PositionSide.FromString s.PositionType
        let result =
            match orderSide, positionSide with
            | BUY,  LONG  -> 
                // let diff = orderBook.BidPrice - s.Price
                orderBook.BidPrice
            | BUY,  SHORT -> orderBook.BidPrice
            | SELL, LONG  -> orderBook.AskPrice
            | SELL, SHORT -> orderBook.AskPrice
            | _           -> raise (exn <| sprintf "Unexpected position type value: '%s' trying to determine order price" s.PositionType)
        return result
    } 
    |> AsyncResult.mapError exn
    |> AsyncResult.catch id

let toExchangeOrder (signalCommand: FuturesSignalCommandView) = 
    let orderSide = findOrderSide (PositionSide.FromString signalCommand.PositionType) (SignalAction.FromString signalCommand.Action)
    Result.map (fun os ->
            {
                ExchangeOrder.CreatedTime = DateTime.UtcNow
                Id = 0L // to be assigned by the data store
                Status = "READY"
                StatusReason = "About to place Order"
                Symbol = string signalCommand.Symbol
                Price = signalCommand.Price
                OriginalQty = signalCommand.Quantity
                ExchangeOrderIdSecondary = string signalCommand.SignalId
                SignalId = signalCommand.SignalId
                SignalCommandId = signalCommand.Id
                UpdatedTime = DateTime.UtcNow
                ExchangeId = signalCommand.ExchangeId
                OrderSide = string os
                LastTradeId = 0L
                ExchangeOrderId = sprintf "TBD-%s" (DateTimeOffset.UtcNow.ToString("yyyy-MMM-ddTHH:mm:ss.fff")) // for uniqueness in the db
                ExecutedPrice = 0M // the order is not yet executed - this should be updated by trade status updates
                ExecutedQty = 0M
                FeeAmount = 0M // initially we've not executed anything - so no fees yet
                FeeCurrency = "" // nothing is executed yet. so we dont know what this is.
            }
        ) orderSide

let calcSlippage orderBookPrice desiredPrice =
    ( orderBookPrice - desiredPrice ) * 100M / desiredPrice

let placeOrder (exchange: IExchange) (s: FuturesSignalCommandView) maxSlippage =
    asyncResult {
        Log.Information ("Placing an order for command {Command} for signal {SignalId} using exchange {Exchange}", s, s.SignalId, exchange.GetType().Name)

        let sym = s.Symbol |> Symbol
        let! orderSide = findOrderSide (PositionSide.FromString s.PositionType) (SignalAction.FromString s.Action)

        let assetQty = Math.Abs(s.Quantity) * 1M<qty> // need to take abs value to ensure the right amount is used for longs/shorts
        let! orderPrice = determineOrderPrice exchange s orderSide
        let slippage = calcSlippage orderPrice s.Price
        if slippage > maxSlippage && s.Action = "OPEN"
        then
            Log.Warning("Not Placing any {OrderSide} order with reduced spread loss. Signal suggested price: {SignalSuggestedPrice}. Order request price: {OrderRequestPrice} as Slippaged crossed {Slippage}",
                orderSide,
                s.Price,
                orderPrice,
                slippage
                )
            return! Result.Error (exn "Not placing order due to high slippage")
        else
            Log.Information("Placing {OrderSide} order with reduced spread loss. Signal suggested price: {SignalSuggestedPrice}. Order request price: {OrderRequestPrice}",
                orderSide,
                s.Price,
                orderPrice
                )
            let orderInput = {
                OrderInputInfo.SignalId = s.SignalId
                OrderSide = orderSide
                Price = orderPrice * 1M<price>
                Symbol = sym
                Quantity = assetQty
                PositionSide = PositionSide.FromString s.PositionType
                OrderType = OrderType.LIMIT
            }

            // TODO: Review this flow and see if we need to indicate that a 'rejected' order can't be simply retried
            let mapOrderError err =
                match err with
                | OrderRejectedError ore ->
                    Log.Error("Error (order rejected by exchange) trying to execute signal command {CommandId}: {Error}", s.Id, ore)
                    ore
                | OrderError oe ->
                    Log.Error("Error trying to execute signal command {CommandId}: {Error}", s.Id, oe)
                    oe
                |> exn

            let! o = exchange.PlaceOrder orderInput |> AsyncResult.mapError mapOrderError

            let! exo = 
                s
                |> toExchangeOrder            
                |> Result.map (updateOrderWith o "Placed order")

            return exo
    } |> AsyncResult.catch id

let private retryCount = 10
let private delay = TimeSpan.FromSeconds(1.0)

// TODO: we need to start identifying what sort of things can be retried, and what can't
// eg. rejected orders won't work unless the inputs are changed

let placeOrderWithRetryOnError (exchange: IExchange) (signalCmd: FuturesSignalCommandView) maxSlippage =
    // retry 'retryCount' times, in quick succession if there is an error placing an order
    let placeOrder' = placeOrder exchange signalCmd 
    placeOrder' |> withRetryOnErrorResult retryCount delay maxSlippage

let queryOrderWithRetryOnError (exchange: IExchange) (orderQuery: OrderQueryInfo) = 
    let queryOrder () =
        async {
            try
                let! orderStatus = exchange.QueryOrder orderQuery
                return Result.Ok orderStatus
            with
            | e -> return (Result.Error e)
        }
    queryOrder |> withRetryOnErrorResult retryCount delay ()

let cancelOrderWithRetryOnError (exchange: IExchange) (orderQuery: OrderQueryInfo) =
    let cancelOrder () =
        exchange.CancelOrder orderQuery
        |> AsyncResult.mapError exn
        |> AsyncResult.catch id
    cancelOrder |> withRetryOnErrorResult retryCount delay ()

let private getLatestOrderState exchange order orderQuery =
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

    getLatestOrderStateWithBackOff waitMillis attempt exchange order orderQuery
    
let getFilledQty (orders: ExchangeOrder seq) = 
    if Seq.isEmpty orders
    then 0M
    else orders |> Seq.sumBy (fun o -> o.ExecutedQty)

let rec executeOrdersForCommand
    (exchange: IExchange)
    (saveOrder: ExchangeOrder -> Async<Result<ExchangeOrder, exn>>)
    (getPositionSize: SignalId -> Async<Result<decimal, exn>>)
    (maxSlippage: decimal)
    (ordersSoFar: ExchangeOrder list)
    (cancellationDelaySeconds: int)
    (maxAttempts: int) 
    (attempt: int)
    (signalCommand: FuturesSignalCommandView) =

    asyncResult {

        // don't pushproperty outside this scope - so the recursive message loop gets it. We dont want that!
        use _ = LogContext.PushProperty("CommandId", signalCommand.Id)
        use _ = LogContext.PushProperty("SignalId", signalCommand.SignalId)
        use _ = LogContext.PushProperty("Exchange", exchange.GetType().Name)

        // need to compare with <= rather than 0, because or possible rounding issues due to some exchanges having a LOT size.
        if attempt > maxAttempts // TODO include slippage condition
        then 
            Log.Information("Done retrying {MaxAttempts} times. Giving up on signal command {originalSignalCommand}...",
                maxAttempts,
                signalCommand)
            return ordersSoFar
        else
            Log.Information ("Starting executeOrder attempt: {Attempt}/{MaxAttempts} for command {Command}", 
                attempt, maxAttempts,
                signalCommand)

            let! commandWithRemainingQty =
                // we need to find out how much more qty to place an order for:
                asyncResult {
                    let! openedPosition = getPositionSize (SignalId signalCommand.SignalId)
                    let cmd = 
                        match signalCommand.Action with
                        | "CLOSE" ->
                            Result.Ok { signalCommand with Quantity = openedPosition } //for now just close the entire position that we have opened
                        | "OPEN" ->
                            // check what we attempted in the previous attempts
                            let executedQtySoFar = getFilledQty ordersSoFar
                            Ok { signalCommand with Quantity = signalCommand.Quantity - executedQtySoFar }
                        | x -> Result.Error (exn <| (sprintf "Unknown signal command action: %s" x)) // TODO model this better
                    return! cmd
                }

            let! exchangeOrder = placeOrderWithRetryOnError exchange commandWithRemainingQty maxSlippage
            let! newOrder = saveOrder exchangeOrder
            Log.Information("Saved new order: {InternalOrderId}. {Order}", newOrder.Id, newOrder)

            Log.Information("Waiting {CancellationDelaySeconds} seconds before querying and cancelling if needed.", cancellationDelaySeconds)
            let _nseconds = cancellationDelaySeconds * 1000 // millis
            do! Async.Sleep _nseconds

            let orderQuery = {
                OrderQueryInfo.OrderId = OrderId newOrder.ExchangeOrderId
                Symbol = Symbol newOrder.Symbol
            }
            Log.Information("Querying order status after waiting... {Query}", orderQuery)

            let! updatedOrder = getLatestOrderState exchange newOrder orderQuery
            
            match updatedOrder.Status with
            | "FILLED" ->
                let! filledOrder' = saveOrder updatedOrder
                Log.Information("Filled order: {ExchangeOrder}", filledOrder')
                return (filledOrder' :: ordersSoFar)
            | _ ->
                Log.Information("Trying to cancel order {ExchangeOrder} (Signal: {SignalId}), because it didn't fill yet. Current status: {OrderStatus}",
                    updatedOrder,
                    updatedOrder.SignalId,
                    updatedOrder.Status)

                let! cancelled = cancelOrderWithRetryOnError exchange orderQuery

                // we need to query again, so we get a potentially updated executedQty
                Log.Information ("Cancel order ({OrderId}) attempted, for signal {SignalId}, command {CommandId}. Success: {Success}. Querying latest status...",
                    newOrder.Id, newOrder.SignalId, signalCommand.Id, cancelled)

                let! updatedOrder' = getLatestOrderState exchange updatedOrder orderQuery 
                let! updatedOrder'' = saveOrder updatedOrder'
                Log.Information ("Saved order after cancellation attempt: {UpdatedOrder}", updatedOrder'')

                if updatedOrder''.ExecutedQty < signalCommand.Quantity
                then
                    Log.Information ("Retrying again - since we didn't fill the original quantity this iteration: Want: {TotalRequiredQty}, FilledSoFar: {FilledQty}",
                        signalCommand.Quantity, updatedOrder''.ExecutedQty)
                    let updatedOrders = updatedOrder'' :: ordersSoFar
                    let! orders = 
                        executeOrdersForCommand 
                            exchange 
                            saveOrder 
                            getPositionSize
                            maxSlippage
                            updatedOrders
                            cancellationDelaySeconds
                            maxAttempts (attempt + 1)
                            signalCommand // send the original command for logging - we always figure out what the right qty remaining is.

                    return orders
                else
                    Log.Information("Looks like we filled everything for order {ExchangeOrder}, for command {SignalCommand}. Signal: {SignalId}",
                        updatedOrder',
                        signalCommand,
                        signalCommand.SignalId)
                    return (updatedOrder':: ordersSoFar) // done, looks like we filled everything!
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

                try
                    let key = s.Id
                    if not <| commandsProcessed.ContainsKey key then
                        commandsProcessed.[key] <- DateTimeOffset.UtcNow

                        let exchangeId = if placeRealOrders then s.ExchangeId else Simulator.ExchangeId
                        let attemptCount = 1
                        let maxAttempts = if s.Action = "OPEN" then 5 else 100
                        let cancellationDelay = if s.Action = "OPEN" then 5 else 5 // seconds
                        let maxSlippage = Strategies.Common.tradePriceSlippageAllowance

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

                        let! result =
                            asyncResult {
                                let! exchange = (lookupExchange (ExchangeId exchangeId) |> Result.mapError exn)
                                let! orders = executeOrdersForCommand exchange saveOrder getPositionSize maxSlippage [] cancellationDelay maxAttempts attemptCount s
                                let executedQty = getFilledQty orders
                                let signalCommandStatus =
                                    if executedQty > 0M
                                    then SignalCommandStatus.SUCCESS
                                    else SignalCommandStatus.FAILED
                                do! completeSignalCommands [(SignalCommandId s.Id)] signalCommandStatus
                            }

                        match result with
                        | Result.Error ex ->
                            Log.Error(ex, "Error processing command: {Command} for signal: {SignalId}", s, s.SignalId)
                        | _ -> ()

                    else
                        Log.Warning ("{Command} for {SignalId} has already been processed. Skipping", s, s.SignalId)

                    replyCh.Reply()
                with e ->
                    Log.Error (e, "Error handling trade msg: for SignalCommand {Command} {SignalId}", s, s.SignalId)

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
    (getFuturesSignalCommands: unit -> Async<FuturesSignalCommandView seq>)
    (completeSignalCommands: SignalCommandId seq -> SignalCommandStatus -> Async<Result<unit, exn>>)
    (getExchangeOrder: int64 -> Async<ExchangeOrder option>)
    (saveOrder: ExchangeOrder -> TradeMode -> Async<Result<int64, exn>>)
    (getPositionSize: SignalId -> Async<Result<decimal, exn>>)
    (placeRealOrders: bool) =

    let saveOrder' exo = 
        async {
            let! exoIdResult = saveOrder exo FUTURES
            return Result.map (fun exoId -> { exo with ExchangeOrder.Id = exoId }) exoIdResult
        }
    
    async {
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
                    | _ -> raise <| exn "Unexpected error: trade agent is not setup"
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
            let cmdIds = oldCommands |> Seq.map(fun s -> SignalCommandId s.Id)
            let! result =  completeSignalCommands cmdIds SignalCommandStatus.EXPIRED
            match result with
            | Result.Error e -> Log.Warning(e, "Error expiring old / stale signal commands. Ignoring...")
            | _ -> ()

        // category 2: newer command is available, so these previousCommands are invalid now
        let previousCommands = signalCommands |> Seq.except latestCommands
        let lapsedStats = previousCommands |> Seq.map(fun s -> s.SignalId, DateTime.UtcNow, s.RequestDateTime, (DateTime.UtcNow - s.RequestDateTime).TotalSeconds)
        if not (previousCommands |> Seq.isEmpty) then
            Log.Debug ("Found {SignalCountThisTick} overridden signals. Lapsed stats: {SignalLapsedStats}. Setting to lapsed: {SignalCommands}.",
                previousCommands |> Seq.length, 
                lapsedStats,
                previousCommands)
            let cmdIds = previousCommands |> Seq.map(fun s -> SignalCommandId s.Id)
            let! result =  completeSignalCommands cmdIds SignalCommandStatus.EXPIRED
            match result with
            | Result.Error e -> Log.Warning(e, "Error expiring previous / overridden signal commands. Ignoring...")
            | _ -> ()
    }
