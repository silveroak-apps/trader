[<AutoOpen>]
module Trader.Exchanges

let knownExchanges =
    [
       // Binance.Futures.Trade.getExchange()
        Bybit.Futures.Trade.getExchange()
        // Simulator.Exchange.getExchange(Binance.Futures.Trade.getExchange()) // TODO refactor this to make it consistent with Binance
    ]
    |> Seq.map (fun exchange -> (exchange.Id, exchange))
    |> dict

let lookupExchange  (exchangeId: Types.ExchangeId) =
    match knownExchanges.TryGetValue exchangeId with
    | true, exchange -> Ok exchange
    | _              -> Error (sprintf "Could not find exchange for: %A" exchangeId)
