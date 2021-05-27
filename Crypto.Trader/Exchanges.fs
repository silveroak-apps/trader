[<AutoOpen>]
module Trader.Exchanges

let knownExchanges = dict [
    ( Binance.Futures.Trade.ExchangeId, Binance.Futures.Trade.getExchange() )
    ( Bybit.Futures.Market.ExchangeId, Bybit.Futures.PositionListener.getExchange1() ) // TODO refactor this to make it consistent with Binance
    // ( Simulator.ExchangeId, Simulator.Exchange.getFutures(Binance.Futures.Trade.getExchange()) )
]

let lookupExchange (Types.ExchangeId exchangeId) = 
    match knownExchanges.TryGetValue exchangeId with
    | true, exchange -> Ok exchange
    | _              -> Error (sprintf "Could not find exchange for exchangeId: %d" exchangeId)