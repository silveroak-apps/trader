module Bybit.Futures.Common

open Exchanges.Common
open Serilog

open FSharp.Linq
open Types
open System
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
let ExchangeName = "ByBitFutures"

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

let coinMClient = BybitCoinMApi(config)   
let usdtClient = BybitUSDTApi(config) 
let private marketApiClient = BybitMarketApi(config)

let getOrderBookCurrentPrice (Symbol s) : Async<Result<Types.OrderBookTickerInfo, string>> =
    async {
        let! o = marketApiClient.MarketOrderbookAsync(s) |> Async.AwaitTask
        let jobj = o :?> Newtonsoft.Json.Linq.JObject
        let obResponse = jobj.ToObject<IO.Swagger.Model.OrderBookBase>()
        let result =
            if obResponse.RetCode ?= 0M && 
                obResponse.Result.Count > 0
            then             
                let buys = obResponse.Result |> Seq.filter(fun obItem -> obItem.Side = "Buy")
                let sells = obResponse.Result |> Seq.filter(fun obItem -> obItem.Side = "Sell")

                match Seq.length buys, Seq.length sells with
                | 0, _ -> Result.Error (sprintf "No buy orders in Bybit orderbook for %s" s)
                | _, 0 -> Result.Error (sprintf "No sell orders in Bybit orderbook for %s" s)
                | _, _ -> 
                    let bestBuy = buys |> Seq.maxBy (fun buy -> buy.Price)
                    let bestSell = sells |> Seq.minBy (fun sell -> sell.Price)
                    let (ticker: OrderBookTickerInfo) = 
                        {
                            AskPrice = Decimal.Parse bestSell.Price
                            AskQty = bestSell.Size.GetValueOrDefault()
                            BidPrice = Decimal.Parse bestBuy.Price
                            BidQty = bestBuy.Size.GetValueOrDefault()
                            Symbol = Symbol s
                        }     
                    Result.Ok ticker

            elif obResponse.RetCode ?= 0M
            then Result.Error (sprintf "No results for Bybit orderbook API call for symbol: %s" s)
            else Result.Error (sprintf "Error getting orderbook from ByBit for symbol (%s): [%A] %s" s obResponse.RetCode obResponse.RetMsg)
        return result
    }

// TODO move this to config / db?
// UnitName is what the 'quantity' refers to in the API calls.
// From there we derive a USD value - if it is USDT, we leave it as is.
// If it is 'CONT' or contracts, we can convert to USD


let usdtSymbols =
   dict [
        (Symbol "BTCUSDT",  { Multiplier = 1 })
     //   (Symbol "ETHUSDT",  { Multiplier = 1 })
     //   (Symbol "DOGEUSDT", { Multiplier = 1 })
        // (Symbol "LTCUSDT", { Multiplier = 1 })
        // (Symbol "LINKUSDT", { Multiplier = 1 })
        // (Symbol "ADAUSDT", { Multiplier = 1 })
        // (Symbol "DOTUSDT", { Multiplier = 1 })
        // (Symbol "UNIUSDT", { Multiplier = 1 })
        // (Symbol "AAVEUSDT", { Multiplier = 1 })
   ]

let coinMSymbols =
   dict [
        (Symbol "BTCUSD", { Multiplier = 1 })
        // (Symbol "ETHUSD", { Multiplier = 1 })
        // (Symbol "EOSUSD", { Multiplier = 1 })
        // (Symbol "XRPUSD", { Multiplier = 1 })
   ]