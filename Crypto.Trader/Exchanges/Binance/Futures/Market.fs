module Binance.Futures.Market

open AnalysisTypes
open FsToolkit.ErrorHandling
open Binance.Futures.Common
open System
open Serilog

let private fromBinanceKLine (s:Symbol) (k: Binance.Net.Interfaces.IBinanceKline): KLine =
    let intervalMinutes = Math.Round((k.CloseTime - k.OpenTime).TotalMinutes) |> int
    {
        KLine.OpenTime = DateTimeOffset (k.OpenTime)
        Open = k.Open
        Close = k.Close
        Low = k.Low
        High = k.High
        Volume = k.QuoteVolume
        IntervalMinutes = intervalMinutes
        Symbol = s
    }

let private getKLineInterval (i: int) =
    match i with
    |      1 -> Ok Binance.Net.Enums.KlineInterval.OneMinute
    |      3 -> Ok Binance.Net.Enums.KlineInterval.ThreeMinutes
    |      5 -> Ok Binance.Net.Enums.KlineInterval.FiveMinutes
    |     15 -> Ok Binance.Net.Enums.KlineInterval.FifteenMinutes
    |     30 -> Ok Binance.Net.Enums.KlineInterval.ThirtyMinutes
    |     60 -> Ok Binance.Net.Enums.KlineInterval.OneHour
    |    120 -> Ok Binance.Net.Enums.KlineInterval.TwoHour
    |    240 -> Ok Binance.Net.Enums.KlineInterval.FourHour
    |    360 -> Ok Binance.Net.Enums.KlineInterval.SixHour
    |    480 -> Ok Binance.Net.Enums.KlineInterval.EightHour
    |    720 -> Ok Binance.Net.Enums.KlineInterval.TwelveHour
    |   1440 -> Ok Binance.Net.Enums.KlineInterval.OneDay
    |  10080 -> Ok Binance.Net.Enums.KlineInterval.OneWeek
    | 320400 -> Ok Binance.Net.Enums.KlineInterval.OneMonth
    | _      -> Result.Error <| KLineError.UnsupportedKlineTypeError (sprintf "Unsupported kline interval %d" i)

let private getKLines (q: KLineQuery) : Async<Result<KLine seq, KLineError>> =    
    asyncResult {
        let client = getBaseClient()
        
        let from = q.OpenTime.DateTime
        let limit = q.Limit 
        let! klineInterval = getKLineInterval q.IntervalMinutes
        let! klineResponse =
                match q.Type with 
                | CoinMFutures -> 
                    client.FuturesCoin.Market.GetKlinesAsync(q.Symbol.ToString(), klineInterval, from, limit = limit)
                    |> Async.AwaitTask
                    |> Async.map Some
                | USDTFutures ->
                    client.FuturesUsdt.Market.GetKlinesAsync(q.Symbol.ToString(), klineInterval, from, limit = limit)
                    |> Async.AwaitTask
                    |> Async.map Some
                | _ -> Async.singleton None

        let result =
            match klineResponse with
            | None -> Result.Error (UnsupportedKlineTypeError <| sprintf "Binance Futures Market does not support %s" (q.Type.ToString()))
            | Some response ->
                if not response.Success
                then Result.Error (Error <| sprintf "%A: %s" response.Error.Code response.Error.Message)
                else
                    if response.Data |> Seq.isEmpty
                    then
                        Log.Warning ("No klines in Binance response")
                    else
                        Log.Debug ("Binance kline response - latest candle for {Symbol} {@LatestCandle}", 
                                    string q.Symbol,
                                    response.Data |> Seq.last)
                    Ok (response.Data |> Seq.map (fromBinanceKLine q.Symbol))

        return! result  
    }

let getMarketDataProvider() = {
        new IMarketDataProvider with
        member __.GetKLines q = getKLines q
    }