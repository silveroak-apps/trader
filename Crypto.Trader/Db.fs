module Db

open DbTypes
open Db.Common
open Serilog
open FSharp.Control
open FSharpx.Control
open Types
open System

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

let private getSignalCommands' () =
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
    FROM signal s
        JOIN signal_command sc ON s.signal_id = sc.signal_id
        JOIN positions fp on s.signal_id = fp.signal_id
    WHERE sc.status = 'CREATED' AND fp.signal_status IN ('CREATED', 'ACTIVE')
    "
    |> get<SignalCommandView>
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
        UPDATE signal_command
        SET status = @Status, action_date_time = now() at time zone 'utc'
        WHERE id = @CommandId
    "
    signalCommands
    |> Seq.map (fun s -> 
        let (SignalCommandId scId) = s
        updateSql, { SignalCommandStatusUpdate.CommandId = scId; Status = string status; } :> obj)
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
            SELECT position_size
            FROM pnl 
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

let private saveOrder' (b: ExchangeOrder) =
    let insertOrderSql =
        "
        INSERT INTO exchange_order(
            order_side, symbol, exchange_id,
            status, status_reason, 
            price, executed_price,
            original_qty, executed_qty, 
            fee_currency, fee_amount,
            exchange_order_id, exchange_order_id_secondary, 
            signal_id, signal_command_id,
            last_trade_id,
            created_time, updated_time
            )
        VALUES (
            @OrderSide, @Symbol, @ExchangeId,
            @Status, @StatusReason,
            @Price, @ExecutedPrice,
            @OriginalQty, @ExecutedQty,
            @FeeCurrency, @FeeAmount,
            @ExchangeOrderId, @ExchangeOrderIdSecondary, 
            @SignalId, @SignalCommandId,
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
            executed_price              = @ExecutedPrice,
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

let private getPosition' (ExchangeId exchangeId) (Symbol s) (p: PositionSide) =
    async {
        let getPositionsSql = 
            "
                SELECT signal_id as SignalId,
                    symbol as Symbol,
                    position_type as PositionType, 
                    exchange_id as ExchangeId,
                    strategy_pair_name as StrategyPairName, 
                    signal_status as SignalStatus,
                    position_status as PositionStatus, 
                    executed_buy_qty as ExecutedBuyQty,
                    pending_buy_qty as PendingBuyQty, 
                    executed_sell_qty as ExecutedSellQty,
                    pending_sell_qty as PendingSellQty, 
                    open_commands_count as OpenCommandsCount,
                    close_commands_count as CloseCommandsCount,
                    pending_commands_count as PendingCommandsCount, 
                    entry_price as EntryPrice,
                    close_price as ClosePrice, 
                    entry_time as EntryTime,
                    exit_time as ExitTime, 
                    pnl as Pnl,
                    pnl_percent as PnlPercent,
                    position_size as PositionSize
                FROM pnl
                WHERE symbol = @Symbol 
                    AND position_type = @PositionSide 
                    AND exchange_id = @ExchangeId
                    AND signal_status IN ('ACTIVE', 'CREATED', 'UNKNOWN')
                ORDER BY signal_id DESC
                LIMIT 1;
            "
        let! positions = 
            getWithParam<PositionPnlView> getPositionsSql (
                {|
                    Symbol = s
                    ExchangeId = exchangeId
                    PositionSide = string p
                |} :> obj)
        return (positions 
                |> Seq.map (fun e -> 
                        {  e with 
                            EntryTime = unspecToUtcKind e.EntryTime
                            ExitTime = unspecToUtcKind e.ExitTime
                        }
                    )
                )
                |> Seq.tryHead
    }

type private DbAgentCommand = 
    // Spot only
    | GetSignalsToBuyOrSell of AsyncReplyChannel<Result<Signal seq, exn>>
    | SetSignalsExpired of Signal seq * AsyncReplyChannel<Result<unit, exn>>

    // Futures only
    | GetPositionSize of SignalId * AsyncReplyChannel<Result<decimal, exn>>
    | GetPosition of ExchangeId * Symbol * PositionSide * AsyncReplyChannel<Result<PositionPnlView option, exn>>
    
    // Common 
    | GetSignalCommands of AsyncReplyChannel<Result<SignalCommandView seq, exn>>

    | SetSignalCommandComplete  of SignalCommandId seq * SignalCommandStatus * AsyncReplyChannel<Result<unit, exn>>

    | SaveOrder of ExchangeOrder * AsyncReplyChannel<Result<int64, exn>>

    | GetOrdersForSignal of int64 * AsyncReplyChannel<Result<ExchangeOrder seq, exn>>
    
    | GetExchangeOrder of int64 * AsyncReplyChannel<Result<ExchangeOrder option, exn>>

    | GetTradedSymbols of int64 * AsyncReplyChannel<Result<ExchangeSymbolAndTradeId seq, exn>>

// using an agent to serialise actions to the db
let private dbAgent =
    MailboxProcessor<DbAgentCommand>.Start (fun inbox ->
        let rec messageLoop() = async {
            let! msg = inbox.Receive()

            try
                match msg with

                | GetSignalsToBuyOrSell replyCh ->
                    let! signals = getSignalsToBuyOrSell' |> withRetry 5
                    replyCh.Reply (Ok signals)

                | SetSignalsExpired (ss, replyCh) ->
                    do! ((fun () -> setSignalsExpired' ss) |> withRetry 5)
                    replyCh.Reply (Ok ())
                
                | GetSignalCommands replyCh ->
                    let! signalCommands = getSignalCommands' |> withRetry 5
                    replyCh.Reply (Ok signalCommands)

                | SetSignalCommandComplete (ids, commandStatus, replyCh) ->
                    do! ((fun () -> setSignalCommandsComplete' ids commandStatus) |> withRetry 5)
                    replyCh.Reply (Ok ())
            
                | SaveOrder (d, replyCh) ->
                    let! orderId = (fun () -> saveOrder' d) |> withRetry 5
                    replyCh.Reply (Ok orderId)
                                
                | GetOrdersForSignal (signalId, replyCh) ->
                    let! orders = getOrdersForSignal' |> withRetry' 5 signalId
                    replyCh.Reply (Ok orders)

                | GetExchangeOrder (id, replyCh) ->
                    let! order = getExchangeOrder' |> withRetry' 5 id
                    replyCh.Reply (Ok order)

                | GetTradedSymbols (exchangeId, replyCh) ->
                    let! symbols = getTradedSymbols' exchangeId
                    replyCh.Reply (Ok symbols)

                | GetPositionSize (signalId, replyCh) ->
                    let! positionSize = getPositionSize' |> withRetry' 5 signalId
                    replyCh.Reply (Ok positionSize)

                | GetPosition (exchangeId, symbol, positionSide, replyCh) ->
                    let! position = getPosition' exchangeId symbol |> withRetry' 5 positionSide
                    replyCh.Reply (Ok position)

            with 
                | e -> 
                    Log.Error (e, "Error handling db command: {DbCommand}", msg)
                    // ugly! but need to properly return error - rather than crash here
                    match msg with
                    | GetSignalsToBuyOrSell replyCh            -> replyCh.Reply (Result.Error e)
                    | SetSignalsExpired (_, replyCh)           -> replyCh.Reply (Result.Error e)
                    | GetSignalCommands replyCh                -> replyCh.Reply (Result.Error e)
                    | SetSignalCommandComplete (_, _, replyCh) -> replyCh.Reply (Result.Error e)
                    | SaveOrder (_, replyCh)                   -> replyCh.Reply (Result.Error e)
                    | GetOrdersForSignal (_, replyCh)          -> replyCh.Reply (Result.Error e)
                    | GetExchangeOrder (_, replyCh)            -> replyCh.Reply (Result.Error e)
                    | GetTradedSymbols (_, replyCh)            -> replyCh.Reply (Result.Error e)
                    | GetPositionSize (_, replyCh)             -> replyCh.Reply (Result.Error e)
                    | GetPosition (_, _, _, replyCh)           -> replyCh.Reply (Result.Error e)

            return! messageLoop()
        }
        messageLoop()
    )

let getSignalsToBuyOrSell () = dbAgent.PostAndAsyncReply GetSignalsToBuyOrSell

let setSignalsExpired signals = dbAgent.PostAndAsyncReply (fun replyCh -> SetSignalsExpired (signals, replyCh))

let saveOrder order = dbAgent.PostAndAsyncReply (fun replyCh -> SaveOrder (order, replyCh))

let getOrdersForSignal signalId = dbAgent.PostAndAsyncReply (fun replyCh -> GetOrdersForSignal (signalId, replyCh))

let getExchangeOrder id = dbAgent.PostAndAsyncReply (fun replyCh -> GetExchangeOrder (id, replyCh))

let getTradedSymbols exchangeId = dbAgent.PostAndAsyncReply (fun replyCh -> GetTradedSymbols (exchangeId, replyCh))

// Common
let getSignalCommands () = 
    dbAgent.PostAndAsyncReply GetSignalCommands

let setSignalCommandsComplete commandIds commandStatus = 
    dbAgent.PostAndAsyncReply (fun replyCh -> SetSignalCommandComplete (commandIds, commandStatus, replyCh))

// Futures
let getPositionSize (signalId: SignalId) = 
    dbAgent.PostAndAsyncReply (fun replyCh -> GetPositionSize (signalId, replyCh))

let getPosition (exchangeId: ExchangeId) (symbol: Symbol) (positionSide: PositionSide) = 
    dbAgent.PostAndAsyncReply (fun replyCh -> GetPosition (exchangeId, symbol, positionSide, replyCh))

