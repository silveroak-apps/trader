[<AutoOpen>]
module Trader.Exchanges

let knownExchanges =
    [
        //Binance.Futures.Trade.getExchange()
        Bybit.Futures.Trade.getExchange()
        // Simulator.Exchange.getExchange(Binance.Futures.Trade.getExchange()) // TODO refactor this to make it consistent with Binance
    ]
    |> Seq.map (fun exchange -> (exchange.Id, exchange))
    |> dict

let lookupExchange  (exchangeId: Types.ExchangeId) =
    match knownExchanges.TryGetValue exchangeId with
    | true, exchange -> Ok exchange
    | _              -> Error (sprintf "Could not find exchange for: %A" exchangeId)

// let coinMSymbols =
//     dict [ 
//         // ("BNBUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
//         // ("BTCUSD_PERP", { Multiplier = 100 }) // 1 cont = 100 USD
//         ("BTCUSD", { Multiplier = 100 }) // 1 cont = 100 USD
//         // ("ETHUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
//         // ("ADAUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
//         // ("DOTUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
//     ]

// let allSymbols =
//     knownExchanges.Keys
//     |> Seq.map (fun key ->
//             let exchange = knownExchanges.[key]
//             exchange.GetSupportedSymbols()
//         )
    // usdtSymbols.Keys 
    // //|> Seq.append coinMSymbols.Keys
    // |> Seq.map Symbol
    // |> Seq.toList