module AnalysisTests

open System
open Xunit
open FsUnit.Xunit
open Strategies
open AnalysisTypes

[<Fact>]
let ``Heiken Ashi is calculated correctly`` () =
    let startTime = DateTimeOffset.Now.AddMinutes(-3.0)
    let klines = [
        {
            KLine.OpenTime = startTime
            Open = 1m
            Close = 2m
            High = 3m
            Low = 0m
            Symbol = Symbol "A"
            IntervalMinutes = 1
            Volume = 1m
        }
        {
            KLine.OpenTime = startTime.AddMinutes(1.0)
            Open = 2m
            Close = 3m
            High = 4m
            Low = 1m
            Symbol = Symbol "A"
            IntervalMinutes = 1
            Volume = 1m
        }
        {
            KLine.OpenTime = startTime.AddMinutes(2.0)
            Open = 3m
            Close = 4m
            High = 5m
            Low = 2m
            Symbol = Symbol "A"
            IntervalMinutes = 1
            Volume = 1m
        }
    ]

    let expectedHAClose = [
        1.5m; 2.5m; 3.5m;
    ]
    let expectedHAOpen = [
        1.5m; 1.5m; 2.5m;
    ]
    let expectedHAHigh = [
        3m; 4m; 5m;
    ]
    let expectedHALow = [
        0m; 1m; 2m;
    ]

    let haKLines = Analysis.heikenAshi klines
    
    let actualHAClose = haKLines |> Seq.map (fun c -> c.Close)
    let actualHAOpen = haKLines |> Seq.map (fun c -> c.Open)
    let actualHAHigh = haKLines |> Seq.map (fun c -> c.High)
    let actualHALow = haKLines |> Seq.map (fun c -> c.Low)

    actualHAClose |> should equal expectedHAClose
    actualHAOpen |> should equal expectedHAOpen
    actualHAHigh |> should equal expectedHAHigh
    actualHALow |> should equal expectedHALow
