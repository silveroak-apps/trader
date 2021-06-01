module Types

open System

let Qty : (decimal -> decimal<qty>) = (*) 1M<qty>
let Price : (decimal -> decimal<price>) = (*) 1M<price>

type OrderId = OrderId of string
with
    override this.ToString() =
        let (OrderId s) = this
        s
    
    static member op_Implicit (OrderId s) : string = s

type ClientOrderId = ClientOrderId of string
with
    override this.ToString() =
        let (ClientOrderId s) = this
        s
    
    static member op_Implicit (ClientOrderId s) : string = s

type OrderStatus = 
    | OrderNew
    | OrderFilled of decimal<qty> * decimal<price>
    | OrderPartiallyFilled of decimal<qty> * decimal<price>
    | OrderCancelled of decimal<qty> * decimal<price>
    | OrderQueryFailed of string
with       
    override this.ToString() =
        match this with
        | OrderNew  -> "NEW"
        | OrderFilled _ -> "FILLED"
        | OrderPartiallyFilled _ -> "PARTIALLY_FILLED"
        | OrderCancelled _ -> "CANCELED"
        | OrderQueryFailed _ -> "QUERY_FAILED"    

    member this.Description () =
        match this with
        | OrderNew  -> "NEW"
        | OrderFilled (execQty, execPrice) -> sprintf "FILLED: : ExecutedQty: %M, ExecutedPrice; %M" (execQty / 1M<qty>) (execPrice / 1M<price>)
        | OrderPartiallyFilled (execQty, execPrice) -> sprintf "PARTIALLY_FILLED: ExecutedQty: %M, ExecutedPrice; %M" (execQty / 1M<qty>) (execPrice / 1M<price>)
        | OrderCancelled (execQty, execPrice) -> sprintf "CANCELED: ExecutedQty: %M, ExecutedPrice; %M" (execQty / 1M<qty>) (execPrice / 1M<price>)
        | OrderQueryFailed s -> sprintf "QUERY_FAILED: %s" s

    static member op_Implicit s : string = s.ToString()

type OrderError = 
    | OrderRejectedError of string
    | OrderError of string
with
    override this.ToString() =
        match this with
        | OrderRejectedError s -> s
        | OrderError s -> s

    static member op_Implicit (oe) : string = 
            match oe with
            | OrderRejectedError s -> s
            | OrderError s -> s

type OrderType =
| LIMIT
| MARKET

type FuturesMarginType =
| ISOLATED
| CROSS
| UNKNOWN
with 
    static member FromString (s: string) = 
        match s.ToLower() with
        | "isolated" -> ISOLATED
        | "cross"    -> CROSS
        | _          -> UNKNOWN

type OrderInputInfo = {
    SignalId: int64
    OrderSide: Types.OrderSide
    Quantity: decimal<qty>
    Price: decimal<price>
    Symbol: Symbol
    PositionSide: Types.PositionSide
    OrderType: OrderType
}

type OrderQueryInfo = {
    OrderId: OrderId
    Symbol: Symbol
}

type OrderInfo = {
    OrderId: OrderId
    ClientOrderId: ClientOrderId
    Time: DateTimeOffset
    Quantity: decimal<qty>
    ExecutedQuantity: decimal<qty>
    Price: decimal<price>
    Symbol: Symbol
    Status: OrderStatus
}

type OrderBookTickerInfo = {
    Symbol: Symbol
    BidPrice: decimal
    BidQty: decimal
    AskPrice: decimal
    AskQty: decimal
}

type ExchangePosition = {
    Leverage: decimal
    Side: PositionSide
    Symbol: Symbol
    EntryPrice: decimal
    MarkPrice: decimal
    MarginType: FuturesMarginType
    Amount: decimal
    RealisedPnL: decimal
    UnRealisedPnL: decimal
    IsolatedMargin: decimal
    LiquidationPrice: decimal
}

type SignalId = SignalId of int64
type SignalCommandId = SignalCommandId of int64
type ExchangeOrderInternalId = ExchangeOrderInternalId of int64
type ExchangeId = ExchangeId of int64

type SignalAction = 
| OPEN
| CLOSE
| INCREASE
| DECREASE
| UNKNOWN
with       
    override this.ToString() =
        match this with
        | OPEN -> "OPEN"
        | CLOSE -> "CLOSE"
        | INCREASE -> "INCREASE"
        | DECREASE -> "DECREASE"
        | UNKNOWN -> "UNKNOWN"
    
    static member FromString (s: string) = 
        match s.ToLower() with
        | "open" -> OPEN
        | "close" -> CLOSE
        | "increase" -> INCREASE
        | "decrease" -> DECREASE
        | _ -> UNKNOWN

type SignalCommandStatus =
| CREATED
| EXPIRED
| FAILED
| SUCCESS
with       
    override this.ToString() =
        match this with
        | CREATED -> "CREATED"
        | EXPIRED -> "EXPIRED"
        | FAILED  -> "FAILED"
        | SUCCESS   -> "SUCCESS"

type PositionCommand = 
    | FuturesPositionUpdate of ExchangeId * ExchangePosition seq
    | FuturesBookPrice of ExchangeId * OrderBookTickerInfo
    | RefreshPositions

type IExchange = 
    abstract member PlaceOrder : OrderInputInfo -> Async<Result<OrderInfo, OrderError>>
    abstract member QueryOrder : OrderQueryInfo -> Async<OrderStatus>
    abstract member CancelOrder : OrderQueryInfo -> Async<Result<bool, string>>
    abstract member GetOrderBookCurrentPrice : string -> Async<Result<OrderBookTickerInfo, string>>
    abstract member Name: string
    abstract member Id: ExchangeId

type IFuturesExchange =
    inherit IExchange
    abstract member GetFuturesPositions: string option -> Async<Result<ExchangePosition seq, string>>
    abstract member TrackPositions: MailboxProcessor<PositionCommand> * string seq -> Async<unit>

