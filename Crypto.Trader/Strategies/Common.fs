module Strategies.Common

let knownMarketDataProviders = dict [
    ( Bybit.Futures.Market.ExchangeId, Bybit.Futures.Market.getMarketDataProvider () )
]
