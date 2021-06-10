module Bybit.Futures.Common

open Exchanges.Common

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

type BybitCoinMApi = IO.Swagger.Api.OrderApi
type BybitUSDTApi = IO.Swagger.Api.LinearOrderApi

type BybitUSDTPositionsApi = IO.Swagger.Api.LinearPositionsApi
type BybitCoinMPositionsApi = IO.Swagger.Api.PositionsApi

type BybitConfig = IO.Swagger.Client.Configuration
type BybitOrderResponse = IO.Swagger.Model.OrderResBase
type ByBitOrderResponseResult = IO.Swagger.Model.OrderRes
type ByBitMarketApi = IO.Swagger.Api.MarketApi
type ByBitOBResponse = IO.Swagger.Model.OrderBookBase
type ByBitOBResultResponse = IO.Swagger.Model.OderBookRes

let ExchangeId = 5L

let cfg = appConfig.GetSection "ByBit"

let getApiKeyCfg () =
    { ApiKey.Key = cfg.Item "FuturesKey"
      Secret = cfg.Item "FuturesSecret" }

let config = 
    let apiKey = getApiKeyCfg () 
    let config = BybitConfig.Default
    config.AddApiKey ("api_key", apiKey.Key)
    config.AddApiKey ("api_secret", apiKey.Secret)
    config