module Exchanges.Common
open System

type ApiKey = {
    Key: string
    Secret: string
}

let getFuturesMode (Symbol symbol) =
    if symbol.EndsWith("usdt", StringComparison.OrdinalIgnoreCase)
    then FuturesMarginMode.USDT
    else COINM