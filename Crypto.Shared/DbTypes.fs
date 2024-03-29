﻿module DbTypes

open System

type Signal = {
    SignalId: int64
    SuggestedPrice: decimal
    Symbol: string
    SignalDateTime: DateTime
    SignalType: string
    UseMargin: bool
    ExchangeId: int64
}

type SignalCommandView = {
    Id: int64
    SignalId: int64
    ExchangeId: int64
    Price: decimal
    Quantity: decimal
    Symbol: string
    Action: string // OPEN / CLOSE
    RequestDateTime: DateTime
    ActionDateTime: DateTime
    PositionType: string // LONG/SHORT
    Leverage: int
    Strategy: string
    Status: string
}

type PositionPnlView = {
    SignalId: int64
    Symbol: string
    PositionType: string
    ExchangeId: int64 
    StrategyPairName: string
    SignalStatus: string
    PositionStatus: string
    ExecutedBuyQty: decimal
    PendingBuyQty: decimal
    ExecutedSellQty: decimal
    PendingSellQty: decimal
    OpenCommandsCount: int64
    CloseCommandsCount: int64
    PendingCommandsCount: int64
    EntryPrice: decimal
    ClosePrice: decimal
    EntryTime: DateTime
    ExitTime: DateTime
    Pnl: decimal
    PnlPercent: decimal
    PositionSize: decimal
}

type ExchangeOrder = {
    Id: int64
    Status: string
    StatusReason: string
    Symbol: string
    Price: decimal
    ExecutedPrice: decimal
    ExchangeOrderId: string
    ExchangeOrderIdSecondary: string
    SignalId: int64
    SignalCommandId: int64
    CreatedTime: DateTime
    UpdatedTime: DateTime
    OriginalQty: decimal
    ExecutedQty: decimal
    FeeAmount: decimal
    FeeCurrency: string
    ExchangeId: int64
    OrderSide: string // buy/sell
    LastTradeId: int64
}

type SignalCommandStatusUpdate = {
    CommandId: int64
    Status: string
}

type SignalStatusUpdate = {
    SignalId: int64
    Status: string
}

type SignalIdParam = {
    SignalId: int64
}

type ExchangeOrderIdParam = {
    Id: int64
}

type ExchangeSymbolAndTradeId = {
    Symbol: string
    TradeId: int64
}