module AnalysisTypes

open System

type KLine = {
    IntervalMinutes: int
    Open: decimal
    Close: decimal
    High: decimal
    Low: decimal
    Volume: decimal
    OpenTime: DateTimeOffset
}

type KLineType =
    | Spot
    | USDTFutures
    | CoinMFutures

type KLineQuery = {
    Symbol: Symbol
    IntervalMinutes: int
    OpenTime: DateTimeOffset
    Limit: int
    Type: KLineType
}

type KLineError =
| Error of string
| UnsupportedKlineTypeError of string
| InvalidResponse of string

type IMarketDataProvider =
    abstract member GetKLines : KLineQuery -> Async<Result<KLine seq, KLineError>>
