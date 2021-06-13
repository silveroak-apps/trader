[<AutoOpen>]
module Types

[<Measure>]
type qty

[<Measure>]
type price

let toLower (s: string) = 
    if System.String.IsNullOrWhiteSpace(s) then s else s.ToLower()

type Symbol = Symbol of string
with
    override this.ToString() =
        let (Symbol s) = this
        s
    
    static member op_Implicit (Symbol s) : string = s

type TradeMode = SPOT | FUTURES | UNKNOWN
with
    static member FromString (s: string) = 
        match toLower s with
        | "spot" -> SPOT
        | "futures" -> FUTURES
        | _ -> UNKNOWN

type OrderSide = BUY | SELL | UNKNOWN
with
    static member FromString (s: string) = 
        match toLower s with
        | "buy" -> BUY
        | "sell" -> SELL
        | _ -> UNKNOWN

type PositionSide = LONG | SHORT | NOT_APPLICABLE
with
    static member FromString (s: string) = 
        match toLower s with
        | "long" -> LONG
        | "short" -> SHORT
        | _ -> NOT_APPLICABLE

type FuturesMarginMode = USDT | COINM
with
    static member FromString (s: string) = 
        match toLower s with
        | "usdt" -> USDT
        | _ -> COINM
