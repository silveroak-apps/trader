module Bybit.Futures.Market

open AnalysisTypes
open FsToolkit.ErrorHandling
open Bybit.Futures.Api
open System

let private fromBybitKLine (k: BybitKLine): KLine =
    let parseInterval i =
        match i with
        | "D" -> 24 * 60
        | "W" -> 7 * 24 * 60
        | "M" -> 30 * 7 * 24 * 60
        | s   -> Int32.Parse s
    {
        KLine.OpenTime = DateTimeOffset.FromUnixTimeSeconds (int64 <| k.OpenTime.GetValueOrDefault())
        Open = k.Open |> Decimal.Parse
        Close = k.Close |> Decimal.Parse
        Low = k.Low |> Decimal.Parse
        High = k.High |> Decimal.Parse
        Volume = k.Volume |> Decimal.Parse
        IntervalMinutes = parseInterval k.Interval
    }

let private getKLines (q: KLineQuery) : Async<Result<KLine seq, KLineError>> =    
    async {
        let from = q.OpenTime.ToUnixTimeSeconds() |> decimal
        let limit = q.Limit |> decimal 

        let! bybitKlineResponse =
                match q.Type with 
                | CoinMFutures -> 
                    let client = BybitCoinMKlineApi()
                    client.KlineGetAsync(q.Symbol.ToString(), string q.IntervalMinutes, from, limit) 
                    |> Async.AwaitTask
                    |> Async.map Some
                | USDTFutures -> 
                    let client = BybitUSDTKlineApi()
                    client.LinearKlineGetAsync(q.Symbol.ToString(), string q.IntervalMinutes, from, limit) 
                    |> Async.AwaitTask
                    |> Async.map Some
                | _ -> Async.singleton None

        match bybitKlineResponse with
        | None -> return Result.Error (UnsupportedKlineTypeError <| q.Type.ToString())
        | Some response ->
            let jobj = response :?> Newtonsoft.Json.Linq.JObject
            let klineResponse = jobj.ToObject<BybitKLineBase>()
            return
                if klineResponse.RetCode <> Nullable 0M
                then Result.Error (KLineError.Error <| sprintf "Error getting klines for %A: %s" q klineResponse.RetMsg)
                else Ok (klineResponse.Result |> Seq.map fromBybitKLine)
            
    }

let getMarketDataProvider() = {
        new IMarketDataProvider with
        member __.GetKLines q = getKLines q
    }