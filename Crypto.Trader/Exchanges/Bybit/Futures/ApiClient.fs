module Bybit.Futures.Types

(*
    Bybit InversePerpetual contracts are like Binance COIN-M margined perpetual contracts
    Bybit LinearPerpetual contracts are like Binance USDT/USD-M margined perpetual contracts
*)

type BybitCoinMKlineApi = IO.Swagger.Api.KlineApi
type BybitUSDTKlineApi  = IO.Swagger.Api.LinearKlineApi

type BybitKLineBase = IO.Swagger.Model.KlineBase
type BybitKLine     = IO.Swagger.Model.KlineRes

type BybitCoinMApi  =  IO.Swagger.Api.OrderApi
type BybitUSDTMApi  = IO.Swagger.Api.LinearOrderApi
type BybitMarketApi = IO.Swagger.Api.MarketApi
type BybitConfig    = IO.Swagger.Client.Configuration
type BybitOrderResponse = IO.Swagger.Model.OrderResBase