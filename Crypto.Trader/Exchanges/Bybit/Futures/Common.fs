module Bybit.Futures.Common

open System.Security.Cryptography
open System.Text

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

type BybitConfig = IO.Swagger.Client.Configuration
type BybitOrderResponse = IO.Swagger.Model.OrderResBase
type ByBitOrderResponseResult = IO.Swagger.Model.OrderRes
type ByBitMarketApi = IO.Swagger.Api.MarketApi
type ByBitOBResponse = IO.Swagger.Model.OrderBookBase
type ByBitOBResultResponse = IO.Swagger.Model.OderBookRes

let ExchangeId = 5L

// let createSignature (secret: string) (parameters: (string * string) list) =

//     let toBytes (s: string) = Encoding.UTF8.GetBytes s

//     let paramBytes =
//         parameters
//         |> List.map (fun (n,v) -> sprintf "%s=%s" n v)
//         |> List.fold (fun s item -> if s = "" then item else sprintf "%s&%s" s item) ""
//         |> toBytes

//     let toString (bs: byte array) = 
//         let hex = new StringBuilder(2 * Array.length bs)
//         bs
//         |> Array.iter (fun b -> hex.AppendFormat("{0:x2}", b) |> ignore)
//         hex.ToString()

//     let hmacsha256 (keyBytes: byte array) (messageBytes: byte array) =
//         use hash = new HMACSHA256 (keyBytes)
//         hash.ComputeHash messageBytes

//     hmacsha256 (toBytes secret) paramBytes
//     |> toString
