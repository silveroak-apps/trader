module Trade.Futures

open System
open System.Collections.Generic
open Serilog
open Serilog.Context
open Binance.Futures
open FSharpx.Control
open FSharp.Control
open FsToolkit.ErrorHandling

open DbTypes
open Types

let private knownExchanges = dict [
    ( Trade.ExchangeId, Trade.getExchange() )
    ( Simulator.ExchangeId, Simulator.Exchange.get(Trade.getExchange()) )
]

let getExchange (exchangeId: int64) = 
    match knownExchanges.TryGetValue exchangeId with
    | true, exchange -> Result.Ok exchange
    | _              -> Result.Error (sprintf "Could not find exchange for exchangeId: %d" exchangeId)

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

let private findOrderSide positionType signalAction = 
    match positionType, signalAction with
    | "LONG", "OPEN"      -> BUY  |> Ok
    | "LONG", "INCREASE"  -> BUY  |> Ok
    | "LONG", "DECREASE"  -> SELL |> Ok
    | "LONG", "CLOSE"     -> SELL |> Ok

    | "SHORT", "OPEN"     -> SELL |> Ok
    | "SHORT", "INCREASE" -> SELL |> Ok
    | "SHORT", "DECREASE" -> BUY  |> Ok
    | "SHORT", "CLOSE"    -> BUY  |> Ok

    | _, _                -> Result.Error <| exn (sprintf "Unsupported orderType: %s, signalAction: %s" positionType signalAction)

let evaluateSignalStatusUpdate signalAction (exchangeOrder: ExchangeOrder) =
    let hasFilledAny = exchangeOrder.ExecutedQty > 0M
    match signalAction, hasFilledAny with
    | "OPEN",  true -> "OPEN"
    | "CLOSE", true -> "CLOSED"
    | _, _          -> "" // no change

let determineOrderPrice (exchange: IExchange) (s: FuturesSignalCommandView) (orderSide: OrderSide) = 
    async {
        try
            match! exchange.GetOrderBookCurrentPrice s.Symbol with
            | None -> return (Result.Error (exn <| sprintf "Could not get latest orderbook price before placing order for %s" s.Symbol))
            | Some orderBook ->
                // intentionally reduce potential loss due to spread
                let result =
                    match orderSide, s.PositionType with
                    | BUY,  "LONG"  -> Result.Ok <| Math.Min(s.Price, orderBook.BidPrice)
                    | BUY,  "SHORT" -> Result.Ok orderBook.BidPrice
                    | SELL, "LONG"  -> Result.Ok orderBook.AskPrice
                    | SELL, "SHORT" -> Result.Ok <| Math.Max(s.Price, orderBook.AskPrice)
                    | _             -> Result.Error (exn <| sprintf "Unexpected position type value: '%s' trying to determine order price" s.PositionType)
                return result
        with
        | e -> return (Result.Error e)
    }

let private toExchangeOrder (signalCommand: FuturesSignalCommandView) = 
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
        UpdatedTime = DateTime.UtcNow
        ExchangeId = signalCommand.ExchangeId
        OrderSide = "TBD"
        LastTradeId = 0L
        ExchangeOrderId = sprintf "TBD-%s" (DateTimeOffset.UtcNow.ToString("yyyy-MMM-ddTHH:mm:ss.fff")) // for uniqueness in the db
        ExecutedPrice = 0M // the order is not yet executed - this should be updated by trade status updates
        ExecutedQty = 0M
        FeeAmount = 0M // initially we've not executed anything - so no fees yet
        FeeCurrency = "" // nothing is executed yet. so we dont know what this is.
    }

let placeOrder (exchange: IExchange) (s: FuturesSignalCommandView) =
    asyncResult {
        // don't pushproperty outside this scope - so the recursive message loop gets it. We dont want that!
        use _ = LogContext.PushProperty("CommandId", s.Id)
        use _ = LogContext.PushProperty("SignalId", s.SignalId)
        use _ = LogContext.PushProperty("ExchangeId", s.ExchangeId)

        Log.Information ("Placing an order for command {Command} for signal {SignalId} using exchange {Exchange}", s, s.SignalId, exchange.GetType().Name)

        let sym = s.Symbol |> Symbol
        let! orderSide = findOrderSide s.PositionType s.Action

        let assetQty = Math.Abs(s.Quantity) * 1M<qty> // need to take abs value to ensure the right amount is used for longs/shorts
        let! orderPrice = determineOrderPrice exchange s orderSide
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

        let mapOrderError err =
            match err with
            | OrderRejectedError ore ->
                Log.Error("Error (order rejected by exchange) trying to execute signal command {CommandId}", s.Id)
                ore
            | OrderError oe ->
                Log.Error("Error trying to execute signal command {CommandId}", s.Id)
                oe
            |> exn

        let! o = exchange.PlaceOrder orderInput |> AsyncResult.mapError mapOrderError

        let exo = 
            s
            |> toExchangeOrder            
            |> updateOrderWith o "Placed order"

        return exo
    } |> AsyncResult.catch id

let private  retryCount = 3
let private delay = TimeSpan.FromSeconds(0.5)

let private placeOrderWithRetryOnError (exchange: IExchange) (signalCmd: FuturesSignalCommandView) =
    // retry 'retryCount' times, in quick succession if there is an error placing an order
    let placeOrder' = placeOrder exchange
    placeOrder' |> withRetryOnErrorResult retryCount delay signalCmd

let private queryOrderWithRetryOnError (exchange: IExchange) (orderQuery: OrderQueryInfo) = 
    let queryOrder () =
        async {
            try
                let! orderStatus = exchange.QueryOrder orderQuery
                return Result.Ok orderStatus
            with
            | e -> return (Result.Error e)
        }
    queryOrder |> withRetryOnErrorResult retryCount delay ()

let private cancelOrderWithRetryOnError (exchange: IExchange) (orderQuery: OrderQueryInfo) =
    let cancelOrder () =
        async {
            try
                return! (exchange.CancelOrder orderQuery) |> AsyncResult.mapError exn
            with
            | ex -> return (Result.Error ex)
        }
    cancelOrder |> withRetryOnErrorResult retryCount delay ()

let private getExecutedQtyForOrder (exchange: IExchange) (orderQuery: OrderQueryInfo) (signalId: string) = 
    async {
        match! queryOrderWithRetryOnError exchange orderQuery with
        | Ok (OrderCancelled (q, p)) -> return Result.Ok (q, p) // we still expect this to return some executed_qty value, if anything was executed
        | Ok (OrderPartiallyFilled (q, p)) -> return Result.Ok (q, p)
        | Ok (OrderFilled (q, p)) -> return Result.Ok (q, p)
        | Ok OrderNew -> 
            return Result.Ok (Qty 0M, Price 0M)
        | Ok (OrderQueryFailed s) -> return Result.Error (exn s)
        | Result.Error exn ->
            Log.Warning (exn, "Order query failed for order {ExchangeOrderId} (Signal {SignalId}): {QueryFailReason}",
                orderQuery.OrderId,
                signalId,
                exn.Message)
            return Result.Error exn
    }

let private getFilledQty (orders: ExchangeOrder seq) = 
    if Seq.isEmpty orders
    then 0M
    else orders |> Seq.sumBy (fun o -> o.ExecutedQty)

let rec executeOrdersForCommand
    (exchange: IExchange)
    (saveOrder: ExchangeOrder -> Async<Result<ExchangeOrder, exn>>)
    (maxSlippage: decimal)
    (ordersSoFar: ExchangeOrder list)
    (maxAttempts: int) 
    (attempt: int)
    (signalCommand: FuturesSignalCommandView) =

    asyncResult {

        // need to compare with <= rather than 0, because or possible rounding issues due to some exchanges having a LOT size.
        if attempt > maxAttempts // TODO include slippage condition
        then 
            Log.Information("Done retrying {MaxAttempts} times. Giving up on order {order}, signal command {originalSignalCommand}...", maxAttempts)
            return ordersSoFar
        else
            Log.Information ("Starting retry/cancel (attempt: {Attempt}/{MaxAttempts}) for command (with updated qty) {Command}, signal {SignalId}", 
                attempt, maxAttempts,
                signalCommand, signalCommand.SignalId)

            let executedQtySoFar = getFilledQty ordersSoFar
            let updatedCommand = { signalCommand with Quantity = signalCommand.Quantity - executedQtySoFar }

            // ugly hack for now - save ExchangeOrder _before_ placing order, so that the TradeStatusListener can find it on a websocket update
            let! _ = saveOrder (toExchangeOrder updatedCommand)

            let! exchangeOrder = placeOrderWithRetryOnError exchange updatedCommand
            let! newOrder = saveOrder exchangeOrder
            let updatedOrders = newOrder :: ordersSoFar

            let _20seconds = 20 * 1000 // millis
            do! Async.Sleep _20seconds

            let orderQuery = {
                OrderQueryInfo.OrderId = OrderId newOrder.ExchangeOrderId
                Symbol = Symbol newOrder.Symbol
            }
            match! (queryOrderWithRetryOnError exchange orderQuery) with
            | OrderFilled (executedQty, executedPrice) -> 
                // save filled status in case we didn't get a socket update
                let filledOrder = {
                    newOrder with
                        Status = "FILLED"
                        StatusReason = "Order filled"
                        UpdatedTime = DateTime.UtcNow
                        ExecutedQty = executedQty / 1M<qty>
                        ExecutedPrice = executedPrice / 1M<price>
                }
                let! _ = saveOrder filledOrder
                return updatedOrders
            | _ -> 
                Log.Information("Trying to cancel order {ExchangeOrder} (Signal: {SignalId}), because it didn't fill yet.",
                    newOrder,
                    newOrder.SignalId)

                do! cancelOrderWithRetryOnError exchange orderQuery

                // it looks like some websockets like Binance don't seem to be sending an update.
                // in any case, we better save the updated order here
                // we need to query again, so we get a potentially updated executedQty
                Log.Information ("Cancel order ({OrderId}) successful, for signal {SignalId}. Querying latest status...",
                    newOrder.Id, newOrder.SignalId)

                let! (executedQty, executedPrice) = getExecutedQtyForOrder exchange orderQuery (string signalCommand.SignalId)
                let cancelledOrder = {
                    newOrder with
                        Status = "CANCELED"
                        StatusReason = "Fill timed out."
                        UpdatedTime = DateTime.UtcNow
                        ExecutedQty = executedQty / 1M<qty>
                        ExecutedPrice = executedPrice / 1M<price>
                }
                let! exo' = saveOrder cancelledOrder
                Log.Information ("Saved order after cancellation: {CancelledOrder}", exo')

                if signalCommand.Quantity < executedQty / 1M<qty>
                then
                    Log.Information ("Retrying again - since we didn't fill the original quantity this iteration: Want: {TotalRequiredQty}, FilledSoFar: {FilledQty}",
                        signalCommand.Quantity, executedQty)
                   
                    let! orders = executeOrdersForCommand exchange saveOrder maxSlippage updatedOrders maxAttempts (attempt + 1) signalCommand 
                    return orders
                else
                    return updatedOrders // done, we filled everything!
    }

type private TradeAgentCommand = 
    | FuturesTrade of FuturesSignalCommandView * bool * AsyncReplyChannel<unit>

// serialising calls to placeOrder to be safe
let private mkTradeAgent 
    (saveOrder: ExchangeOrder -> Async<Result<ExchangeOrder, exn>>)
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
                        let maxSlippage = 0.15M // %

                        (*
                        1 find our which exchange to place order
                        2 find latest orderbook price from exchange (handle errors + 3 retries to get latest price) 
                        3 place order (handle network errors, Binance API errors etc and retry upto 3 times OR less -> till we get an orderID back)
                        4 save order (handle errors + 3 retries for db errors)
                        5 wait for order to fill for 'x' seconds
                        6 query order (handle errors + 3 retries) 
                        6 if filled, save order (handle errors + 3 retries for db errors) --> done
                        7 if not filled, cancel order (handle errors + 3 retries for network/Binance errors)
                        8 if not filled, repeat steps from 2 -> with a new order, and updated qty (only use remaining qty) : repeat 'n' times for OPEN, 'm' times for CLOSE
                        9 if retries at any stage are exhausted, give up
                        10 if we manage to fill order fully, saveOrder (the new ones that happen during retries + handle db save errors)
                        
                        *)

                        let! result =
                            asyncResult {
                                let! exchange = (getExchange exchangeId |> Result.mapError exn)
                                let! orders = executeOrdersForCommand exchange saveOrder maxSlippage [] maxAttempts attemptCount s
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
            tradeAgent <- Some <| mkTradeAgent saveOrder' completeSignalCommands

        do! 
            validCommands
            |> AsyncSeq.ofSeq
            |> AsyncSeq.iterAsync (
                fun s -> 
                    match tradeAgent with
                    | Some agent -> agent.PostAndAsyncReply (fun replyCh -> FuturesTrade (s, placeRealOrders, replyCh))
                    | _ -> raise <| exn "Unexpected error: trade agent is not setup"
                )
        // now we need to 'expire' / invalidate commands that won't be run
        // there are two categories of invalid commands:
        // 1. Too old
        // 2. A newer command is available (what should we do in this case?)

        // category 1: too old / stale
        let lapsedStats = oldCommands |> Seq.map(fun s -> s.SignalId, DateTime.UtcNow, s.RequestDateTime, (DateTime.UtcNow - s.RequestDateTime).TotalSeconds)
        if not (oldCommands |> Seq.isEmpty) then
            Log.Debug ("Found {SignalCountThisTick} old signals. Setting to lapsed: {SignalCommands}. {SignalLapsedStats}", oldCommands, lapsedStats)
            let cmdIds = oldCommands |> Seq.map(fun s -> SignalCommandId s.Id)
            let! result =  completeSignalCommands cmdIds SignalCommandStatus.EXPIRED
            match result with
            | Result.Error e -> Log.Warning(e, "Error expiring old / stale signal commands. Ignoring...")
            | _ -> ()
        
        // category 2: newer command is available, so these previousCommands are invalid now
        let previousCommands = signalCommands |> Seq.except latestCommands
        let lapsedStats = previousCommands |> Seq.map(fun s -> s.SignalId, DateTime.UtcNow, s.RequestDateTime, (DateTime.UtcNow - s.RequestDateTime).TotalSeconds)
        if not (previousCommands |> Seq.isEmpty) then
            Log.Debug ("Found {SignalCountThisTick} overridden signals. Setting to lapsed: {SignalCommands}. {SignalLapsedStats}", previousCommands, lapsedStats)
            let cmdIds = previousCommands |> Seq.map(fun s -> SignalCommandId s.Id)
            let! result =  completeSignalCommands cmdIds SignalCommandStatus.EXPIRED
            match result with
            | Result.Error e -> Log.Warning(e, "Error expiring previous / overridden signal commands. Ignoring...")
            | _ -> ()

        // // listen will handle websocket updates, but will maintain state to open only one connection
        if validCommands |> Seq.length > 0 && placeRealOrders
        then TradeStatusListener.listen getExchangeOrder saveOrder |> ignore // ugly: TradeStatusListener is currently specific to Binance, but the rest of the module is reasonably abstracted
    }
