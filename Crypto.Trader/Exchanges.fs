[<AutoOpen>]
module Trader.Exchanges

let knownExchanges = dict [
    ( Binance.Futures.Trade.ExchangeId, Binance.Futures.Trade.getExchange() )
    // ( Bybit.Futures.Trade.ExchangeId, Bybit.Futures.Trade.getExchange() )
    // ( Simulator.ExchangeId, Simulator.Exchange.getFutures(Binance.Futures.Trade.getExchange()) )
]

let lookupExchange (Types.ExchangeId exchangeId) = 
    match knownExchanges.TryGetValue exchangeId with
    | true, exchange -> Ok exchange
    | _              -> Error (sprintf "Could not find exchange for exchangeId: %d" exchangeId)