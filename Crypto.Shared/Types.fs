[<AutoOpen>]
module Types

[<Measure>]
type qty

[<Measure>]
type price

type Symbol = Symbol of string
with
    override this.ToString() =
        let (Symbol s) = this
        s
    
    static member op_Implicit (Symbol s) : string = s

type TradeMode = SPOT | FUTURES | UNKNOWN
with
    static member FromString (s: string) = 
        match s.ToLower() with
        | "spot" -> SPOT
        | "futures" -> FUTURES
        | _ -> UNKNOWN

type OrderSide = BUY | SELL | UNKNOWN
with
    static member FromString (s: string) = 
        match s.ToLower() with
        | "buy" -> BUY
        | "sell" -> SELL
        | _ -> UNKNOWN