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
        1.5m; 1.5m; 2.0m;
    ]
    let expectedHAHigh = [
        3m; 4m; 5m;
    ]
    let expectedHALow = [
        0m; 1m; 2m;
    ]

    let haKLines = Analysis.heikenAshi klines |> Seq.toList
    
    let actualHAClose = haKLines |> List.map (fun c -> c.Close)
    let actualHAOpen = haKLines |> List.map (fun c -> c.Open)
    let actualHAHigh = haKLines |> List.map (fun c -> c.High)
    let actualHALow = haKLines |> List.map (fun c -> c.Low)

    actualHAClose |> should matchList expectedHAClose
    actualHAOpen |> should matchList expectedHAOpen
    actualHAHigh |> should matchList expectedHAHigh
    actualHALow |> should matchList expectedHALow


type StopLossTests (output: Xunit.Abstractions.ITestOutputHelper) =

    [<Fact>]
    member __.``Stoploss is calculated correctly`` () =
        let gainValues = 
            None :: ([ -0.5m; 0m; 0.25m; 0.33m; 0.50m; 0.75m; 1.0m; 1.25m; 1.5m; 1.75m; 2.0m ] |> List.map Some)
            |> List.pairwise

        // test data matches story 127 as of 3/Jun/2021 (this commit)
        let leverage = 5m // 5x        
        let expectedSLValues =
            [
                -1.0000m;
                -1.0000m;
                -0.5500m;
                -0.2761m;
                 0.1000m;
                 0.4833m;
                 0.8000m;
                 1.0900m;
                 1.3667m;
                 1.6357m;
                 1.9000m;
            ] |> List.map (fun sl -> Math.Round(sl * leverage, 2))

        let mkPosition leverage prevPnlPercent prevSLPercent : Strategies.FuturesPositionAnalyser.PositionAnalysis =
            // most values don't matter for this test
            {
                Strategies.FuturesPositionAnalyser.PositionAnalysis.IsolatedMargin = 0M
                EntryPrice = 0M
                Symbol = Symbol "does not matter"
                Leverage = leverage
                LiquidationPrice = 0M
                MarginType = Types.FuturesMarginType.ISOLATED 
                PositionSide = PositionSide.LONG
                PositionAmount = 0M
                MarkPrice = 0M
                RealisedPnl = None
                UnrealisedPnl = 0M
                CalculatedPnl = None 
                CalculatedPnlPercent = prevPnlPercent
                StoplossPnlPercentValue = prevSLPercent
                IsStoppedOut = false
                CloseRaisedTime = None
            }

        List.zip gainValues expectedSLValues
        |> List.iter (fun ((previousGain, gain), expectedSL) ->
            let previousSL = None
            let position = mkPosition leverage previousGain previousSL
            let newSL = Strategies.FuturesPositionAnalyser.calculateStopLoss position gain
            let actualSL = newSL |> Option.defaultValue Decimal.MinValue
            output.WriteLine (sprintf "Testing with prevGain: %A, gain: %A, prevSL: %A, expectedSL: %A. actual SL: %A" previousGain gain previousSL expectedSL actualSL)
            actualSL |> should equal expectedSL
        )