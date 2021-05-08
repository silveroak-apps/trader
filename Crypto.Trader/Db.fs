module Db

open DbTypes
open Db.Common
open Serilog
open FSharp.Control
open FSharpx.Control
open Types
open System

let private getSpotSignalUpdate (b: ExchangeOrder) =
    let updateSignalSql = 
        if String.Equals(b.OrderSide, "BUY", StringComparison.OrdinalIgnoreCase) then
            "
            UPDATE positive_signal 
            SET status           = @Status,
                actual_buy_price = @Price,
                buy_date_time    = @DateTime
            WHERE signal_id      = @SignalId
                AND status NOT IN ('BUY_CANCELLED', 'BUY_FILLED', 'SELL', 'SELL_READY', 'SELL_NEW', 'SELL_STUCK', 'SELL_FILLED') 
                -- these statuses mean the buy side is already finalised
            "
        else
            "
            UPDATE positive_signal 
            SET status            = @Status,
                actual_sell_price = @Price,
                sell_date_time     = @DateTime
            WHERE signal_id      = @SignalId
                AND status NOT IN ('SELL_STUCK', 'SELL_FILLED')
                -- these statuses is already finalised 
            "
    let actualOrderTime = 
        // these statuses are not yet considered active statuses for an order
        let isInActive =
            [ "READY"; "ERROR"; "REJECTED"; ]
            |> List.tryFind (fun s -> String.Equals(b.Status, s, StringComparison.OrdinalIgnoreCase))

        if isInActive.IsSome then Nullable<DateTime>() else Nullable<DateTime> b.UpdatedTime

    let signalUpdateParams = 
        dict [
          ("Status"  , (sprintf "%s_%s" b.OrderSide b.Status) :> obj) // this will be BUY_FILLED or SELL_FILLED, etc
          ("Price"   , b.ExecutedPrice :> obj)
          ("DateTime", actualOrderTime :> obj)
          ("SignalId", b.SignalId :> obj)
        ]
    [ (updateSignalSql, signalUpdateParams :> obj) ]

let private getTradedSymbols' exchangeId = 
    getWithParam<ExchangeSymbolAndTradeId> "
            SELECT o.symbol, COALESCE(MAX(o.last_trade_id), 0) AS tradeId
            FROM exchange_order o
            WHERE o.exchange_id = @ExchangeId
            GROUP BY o.symbol" (dict [ ("ExchangeId", exchangeId :> obj) ])

let private getSignalsToBuyOrSell' () =
    "
    SELECT
        s.signal_id as SignalId,
        s.buy_price as SuggestedPrice,
        s.symbol as Symbol,
        s.buy_signal_date_time as SignalDateTime,
        'BUY' as SignalType,
        s.usemargin as UseMargin,
        s.exchange_id as ExchangeId
    FROM positive_signal s
        LEFT JOIN positive_signal existing_active_signal ON s.symbol = existing_active_signal.symbol
            AND existing_active_signal.status IN ('BUY', 'BUY_NEW', 'BUY_PARTIALLY_FILLED', 'BUY_FILLED', 'SELL', 'SELL_NEW', 'SELL_PARTIALLY_FILLED')
    WHERE s.status = 'BUY'
        AND (existing_active_signal.signal_id IS NULL OR existing_active_signal.signal_id = s.signal_id) -- allow the latest buy signal itself

    UNION 

    SELECT
        signal_id as SignalId,
        sell_price as SuggestedPrice,
        symbol as Symbol,
        sell_signal_date_time as SignalDateTime,
        'SELL' as SignalType,
        usemargin as UseMargin,
        exchange_id as ExchangeId
    FROM positive_signal s
    WHERE s.status = 'SELL'
    "
    |> get<Signal>
    |> Async.map (Seq.map (fun s -> { s with SignalDateTime = unspecToUtcKind s.SignalDateTime }))

let private getFuturesSignalCommands' () =
    "
    SELECT
        sc.id as Id,
        s.signal_id as SignalId,
        s.exchange_id as ExchangeId,
        sc.price as Price,
        sc.quantity as Quantity,
        s.symbol as Symbol,
        sc.signal_action as Action,
        sc.request_date_time as RequestDateTime,
        sc.action_date_time as ActionDateTime,
        s.position_type as PositionType,
        sc.leverage as Leverage,
        sc.strategy_name as Strategy,
        sc.status as Status
    FROM futures_signal s
        JOIN futures_signal_command sc ON s.signal_id = sc.signal_id
        JOIN futures_positions fp on s.signal_id = fp.signal_id
    WHERE sc.status = 'CREATED' AND fp.signal_status IN ('CREATED', 'ACTIVE')
    "
    |> get<FuturesSignalCommandView>
    |> Async.map (Seq.map (fun s -> { s with RequestDateTime = unspecToUtcKind s.RequestDateTime }))

let private setSignalsExpired' (signals: Signal seq) =
    let updateSql = "
        UPDATE positive_signal
        SET status = 'SIGNAL_LAPSED'
        WHERE signal_id = @SignalId;
        "
    signals
    |> Seq.map (fun s -> updateSql, { SignalIdParam.SignalId = s.SignalId } :> obj)
    |> save

let private setSignalCommandsComplete' (signalCommands: SignalCommandId seq) (status: SignalCommandStatus) =
    let updateSql = "
        UPDATE futures_signal_command
        SET status = @Status, action_date_time = now() at time zone 'utc'
        WHERE id = @CommandId
    "
    signalCommands
    |> Seq.map (fun s -> 
        let (SignalCommandId scId) = s
        updateSql, { FuturesSignalCommandStatusUpdate.CommandId = scId; Status = string status; } :> obj)
    |> save

let private getOrdersForSignal' (signalId: int64) = 
    let getOrdersSql = 
        "
        SELECT id, status, status_reason as StatusReason, 
            symbol, price, executed_price as ExecutedPrice, exchange_order_id as ExchangeOrderId,
            exchange_order_id_secondary as ExchangeOrderIdSecondary,
            signal_id as SignalId, created_time as CreatedTime, updated_time as UpdatedTime,
            original_qty as OriginalQty, executed_qty as ExecutedQty,
            fee_amount as FeeAmount, fee_currency as FeeCurrency,
            exchange_id as ExchangeID, order_side as OrderSide, last_trade_id as LastTradeId       
        FROM exchange_order
        WHERE signal_id = @SignalId
        ORDER BY id DESC
        "
    async {
        let! orders = 
            getWithParam<ExchangeOrder> getOrdersSql ({ SignalIdParam.SignalId = signalId } :> obj)
        return (orders 
                |> Seq.map (fun e -> 
                        {  e with 
                            CreatedTime = unspecToUtcKind e.CreatedTime
                            UpdatedTime = unspecToUtcKind e.UpdatedTime
                        }
                    )
                )
    }

let private getPositionSize' (SignalId signalId) =
    async {
        let positionSql = "
            SELECT
            	CASE
            		WHEN position_type = 'LONG' THEN coalesce (executed_buy_qty, 0) - coalesce (executed_sell_qty, 0)
            		WHEN position_type = 'SHORT' THEN coalesce (executed_sell_qty, 0) - coalesce (executed_buy_qty, 0)
            		ELSE -1
            	END AS position_size
            FROM futures_positions 
            WHERE signal_id = @SignalId
        "
        let! result = getWithParam<decimal> positionSql ({ SignalIdParam.SignalId = signalId } :> obj)
        let positionSize =
            if Seq.isEmpty result
            then -1M
            else Seq.head result
        return positionSize
    }

let private getExchangeOrder' (id: int64) = 
    let getOrderSql = 
        "
        SELECT id, status, status_reason as StatusReason, 
            symbol, price, executed_price as ExecutedPrice, exchange_order_id as ExchangeOrderId,
            exchange_order_id_secondary as ExchangeOrderIdSecondary,
            signal_id as SignalId, created_time as CreatedTime, updated_time as UpdatedTime,
            original_qty as OriginalQty, executed_qty as ExecutedQty,
            fee_amount as FeeAmount, fee_currency as FeeCurrency,
            exchange_id as ExchangeID, order_side as OrderSide, last_trade_id as LastTradeId            
        FROM exchange_order
        WHERE id = @Id
        "
    async {
        let! orders = 
            getWithParam<ExchangeOrder> getOrderSql ({ 
                ExchangeOrderIdParam.Id = id
            } :> obj)
        return (orders 
                |> Seq.map (fun e -> 
                        {  e with 
                            CreatedTime = unspecToUtcKind e.CreatedTime
                            UpdatedTime = unspecToUtcKind e.UpdatedTime
                        }
                    )
                )
                |> Seq.tryHead
    }

let private saveOrderAndSignal (b: ExchangeOrder) (signalUpdates: seq<string * obj>) =
    let insertOrderSql =
        "
        INSERT INTO exchange_order(
            order_side, symbol, exchange_id,
            status, status_reason, 
            price, executed_price,
            original_qty, executed_qty, 
            fee_currency, fee_amount,
            exchange_order_id, exchange_order_id_secondary, signal_id, 
            last_trade_id,
            created_time, updated_time
            )
        VALUES (
            @OrderSide, @Symbol, @ExchangeId,
            @Status, @StatusReason,
            @Price, @ExecutedPrice,
            @OriginalQty, @ExecutedQty,
            @FeeCurrency, @FeeAmount,
            @ExchangeOrderId, @ExchangeOrderIdSecondary, @SignalId, 
            @LastTradeId,
            @CreatedTime, @UpdatedTime
           )
        RETURNING id
        "
    let updateOrderSql = 
        "
        UPDATE exchange_order 
        SET
            status                      = @Status,
            status_reason               = @StatusReason,
            executed_price              = @Price,
            executed_qty                = @ExecutedQty,
            fee_currency                = @FeeCurrency,
            fee_amount                  = @FeeAmount,
            updated_time                = @UpdatedTime,
            last_trade_id               = @LastTradeId,
            exchange_order_id           = @ExchangeOrderId,
            exchange_order_id_secondary = 
                CASE 
                    WHEN exchange_order_id_secondary = cast(signal_id as varchar) THEN exchange_order_id_secondary -- dont change
                    ELSE @ExchangeOrderIdSecondary
                END  -- we may have manually sold on the web, but manually linked up to a known signal later. Handle those cases here
        WHERE id                        = @Id
            AND (
                status NOT IN ('FILLED', 'REJECTED') -- this is already finalised. don't update it anymore
                OR
                (status = 'FILLED' AND status = @Status AND last_trade_id = 0) -- unless last_trade_id wasn't set, and incoming status same as existing final status
            )
        RETURNING id
        "

    async {
        use cnn = mkConnection ()
        do! cnn.OpenAsync() |> Async.AwaitTask
        use tx = cnn.BeginTransaction ()

        try
            if not <| (signalUpdates |> Seq.isEmpty)
            then
                do! saveUsing cnn signalUpdates

            let insertOrUpdateSql = if b.Id > 0L then updateOrderSql else insertOrderSql
            // this get is actually a 'save' order and return id
            let! orderId = getWithConnectionAndParam<int64> cnn insertOrUpdateSql b |> Async.map Seq.tryHead
            
            tx.Commit()
            return (orderId |> Option.defaultValue b.Id)
        with e -> 
            tx.Rollback()
            Log.Error (e, "Error saving order: {Order}. (Tried to {Operation}).",
                b,
                if b.Id > 0L then "UPDATE" else "INSERT")            
            return -1L
    }

let private saveOrder' (exo: ExchangeOrder) (t: TradeMode) =
    async {
        let! signalUpdates =
            match t with
            | SPOT -> async { return (getSpotSignalUpdate exo) }
            | _ -> async { return [] }

        let! orderId = saveOrderAndSignal exo signalUpdates
        return orderId
    }

type private DbAgentCommand = 
    // Spot only
    | GetSignalsToBuyOrSell of AsyncReplyChannel<Signal seq>
    | SetSignalsExpired of Signal seq * AsyncReplyChannel<unit>

    // Futures only
    | GetFuturesSignalCommands of AsyncReplyChannel<FuturesSignalCommandView seq>
    | SetSignalCommandComplete  of SignalCommandId seq * SignalCommandStatus * AsyncReplyChannel<unit>
    | GetPositionSize of SignalId * AsyncReplyChannel<decimal>
    
    // Common 
    // Save order handles signal updates too :/
    | SaveOrder of ExchangeOrder  * TradeMode * AsyncReplyChannel<int64>

    | GetOrdersForSignal of int64 * AsyncReplyChannel<ExchangeOrder seq>
    
    | GetExchangeOrder of int64 * AsyncReplyChannel<ExchangeOrder option>

    | GetTradedSymbols of int64 * AsyncReplyChannel<ExchangeSymbolAndTradeId seq>

// using an agent to serialise actions to the db
let private dbAgent =
    MailboxProcessor<DbAgentCommand>.Start (fun inbox ->
        let rec messageLoop() = async {
            let! msg = inbox.Receive()

            try
                match msg with

                | GetSignalsToBuyOrSell replyCh ->
                    let! signals = getSignalsToBuyOrSell' |> withRetry 5
                    replyCh.Reply signals

                | SetSignalsExpired (ss, replyCh) ->
                    do! ((fun () -> setSignalsExpired' ss) |> withRetry 5)
                    replyCh.Reply ()
                
                | GetFuturesSignalCommands replyCh ->
                    let! signalCommands = getFuturesSignalCommands' |> withRetry 5
                    replyCh.Reply signalCommands

                | SetSignalCommandComplete (ids, commandStatus, replyCh) ->
                    do! ((fun () -> setSignalCommandsComplete' ids commandStatus) |> withRetry 5)
                    replyCh.Reply ()
            
                | SaveOrder (d, t, replyCh) ->
                    let! orderId = (fun () -> saveOrder' d t) |> withRetry 5
                    replyCh.Reply orderId
                                
                | GetOrdersForSignal (signalId, replyCh) ->
                    let! orders = getOrdersForSignal' |> withRetry' 5 signalId
                    replyCh.Reply orders

                | GetExchangeOrder (id, replyCh) ->
                    let! order = getExchangeOrder' |> withRetry' 5 id
                    replyCh.Reply order

                | GetTradedSymbols (exchangeId, replyCh) ->
                    let! symbols = getTradedSymbols' exchangeId
                    replyCh.Reply symbols

                | GetPositionSize (signalId, replyCh) ->
                    let! positionSize = getPositionSize' |> withRetry' 5 signalId
                    replyCh.Reply positionSize
            with 
                | e ->
                    Log.Error (e, "Error handling db command: {DbCommand}", msg)
                    raise e

            return! messageLoop()
        }
        messageLoop()
    )

let getSignalsToBuyOrSell () = dbAgent.PostAndAsyncReply GetSignalsToBuyOrSell

let setSignalsExpired signals = dbAgent.PostAndAsyncReply (fun replyCh -> SetSignalsExpired (signals, replyCh))

let saveOrder order tradeMode = 
    async {
        try
            let! result = dbAgent.PostAndAsyncReply (fun replyCh -> SaveOrder (order, tradeMode, replyCh))
            return Ok result
        with
            | e -> return Result.Error e
    }

let getOrdersForSignal signalId = dbAgent.PostAndAsyncReply (fun replyCh -> GetOrdersForSignal (signalId, replyCh))

let getExchangeOrder id = dbAgent.PostAndAsyncReply (fun replyCh -> GetExchangeOrder (id, replyCh))

let getTradedSymbols exchangeId = dbAgent.PostAndAsyncReply (fun replyCh -> GetTradedSymbols (exchangeId, replyCh))

// Futures
let getFuturesSignalCommands () = dbAgent.PostAndAsyncReply (fun replyCh -> GetFuturesSignalCommands (replyCh))

let setSignalCommandsComplete commandIds commandStatus = 
    async {
        try
            do! dbAgent.PostAndAsyncReply (fun replyCh -> SetSignalCommandComplete (commandIds, commandStatus, replyCh))
            return (Ok ())
        with
        | e -> return Result.Error e
    }

let getPositionSize (signalId: SignalId) = 
    async {
        try
            let! result = dbAgent.PostAndAsyncReply (fun replyCh -> GetPositionSize (signalId, replyCh))
            return Ok result
        with
            | e -> return Result.Error e
    }
