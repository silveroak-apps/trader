module Bybit.Futures.Common

(*
    Bybit InversePerpetual contracts are like Binance COIN-M margined perpetual contracts
    Bybit LinearPerpetual contracts are like Binance USDT/USD-M margined perpetual contracts
*)

type BybitCoinMKlineApi = IO.Swagger.Api.KlineApi
type BybitUSDTKlineApi = IO.Swagger.Api.LinearKlineApi

type BybitKLineBase = IO.Swagger.Model.KlineBase
type BybitKLine = IO.Swagger.Model.KlineRes

type BybitTradeApi = IO.Swagger.Api.ExecutionApi
type BybitMarketApi = IO.Swagger.Api.MarketApi

let ExchangeId = 5L