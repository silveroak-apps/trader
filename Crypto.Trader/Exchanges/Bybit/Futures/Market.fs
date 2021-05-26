module Bybit.Futures.Market

open AnalysisTypes
open FsToolkit.ErrorHandling
open Bybit.Futures.Api
open System

let private getKLines (q: KLineQuery) : Async<Result<KLine seq, KLineError>> =
    //let q = { KLineQuery.IntervalMinutes = 1; Symbol = Symbol "BTCUSDT"; OpenTime = DateTimeOffset.Now.AddMinutes(-2.0); Limit = 10; }  
    async {
        let client = BybitUSDTKlineApi()
        let from = q.OpenTime.ToUnixTimeSeconds() |> decimal
        let limit = q.Limit |> decimal
        let! response = client.LinearKlineGetAsync(q.Symbol.ToString(), string q.IntervalMinutes, from, limit) |> Async.AwaitTask
        let jobj = response :?> Newtonsoft.Json.Linq.JObject
        let klines = jobj.ToObject<BybitKLineBase>() //.Result
        printfn "response:\n%A" klines
        return (Result.Error (KLineError.Error "TODO implement"))
    }

let getMarketDataProvider() = {
        new IMarketDataProvider with
        member __.GetKLines q = getKLines q
    }