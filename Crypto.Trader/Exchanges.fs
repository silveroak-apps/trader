[<AutoOpen>]
module Trader.Exchanges

let knownExchanges =
    [
        Binance.Futures.Trade.getExchange()
        //Bybit.Futures.PositionListener.getExchange1()
        // Simulator.Exchange.getExchange(Binance.Futures.Trade.getExchange()) // TODO refactor this to make it consistent with Binance
    ]
    |> Seq.map (fun exchange -> (exchange.Id, exchange))
    |> dict

let lookupExchange  (exchangeId: Types.ExchangeId) =
    match knownExchanges.TryGetValue exchangeId with
    | true, exchange -> Ok exchange
    | _              -> Error (sprintf "Could not find exchange for: %A" exchangeId)

type ContractDetails = {
    Multiplier: int // i.e if it is contracts, how many USD is 1 contract
}

// TODO move this to config / db?
// UnitName is what the 'quantity' refers to in the API calls.
// From there we derive a USD value - if it is USDT, we leave it as is.
// If it is 'CONT' or contracts, we can convert to USD
let usdtSymbols  = 
    dict [ 
        // ("BNBUSDT", { Multiplier = 1 })
        // ("BTCUSDT", { Multiplier = 1 })
        // ("ETHUSDT", { Multiplier = 1 })
        // ("ADAUSDT", { Multiplier = 1 })
        // ("DOTUSDT", { Multiplier = 1 })
        // ("DOGEUSDT", { Multiplier = 1 })
        ("MATICUSDT", { Multiplier = 1 })
        // ("LUNAUSDT", { Multiplier = 1 })
    ]

let coinMSymbols =
    dict [ 
        // ("BNBUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
        ("BTCUSD_PERP", { Multiplier = 100 }) // 1 cont = 100 USD
        //("BTCUSD", { Multiplier = 100 }) // 1 cont = 100 USD
        // ("ETHUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
        // ("ADAUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
        // ("DOTUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
    ]

let allSymbols =
    usdtSymbols.Keys 
    |> Seq.append coinMSymbols.Keys 
    |> Seq.toList