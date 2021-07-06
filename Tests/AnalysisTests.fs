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

    let mkPosition leverage prevPnlPercent prevSLPercent : Strategies.FuturesPositionAnalyser.PositionAnalysis =
        // most values don't matter for this test
        {
            Strategies.FuturesPositionAnalyser.PositionAnalysis.IsolatedMargin = 0M
            ExchangeId = Types.ExchangeId 1L
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
            PositionInDb = None
        }

    let runTest leverage gainValuesWithLeverage previousSLValuesWithLeverage expectedSLValues = 
        List.zip3 gainValuesWithLeverage previousSLValuesWithLeverage expectedSLValues
        |> List.iter (fun ((previousGain, gain), previousSL, expectedSL) ->
            let position = mkPosition leverage previousGain previousSL
            let newSL = Strategies.FuturesPositionAnalyser.calculateStopLoss position gain
            let actualSL = newSL |> Option.defaultValue Decimal.MinValue
            output.WriteLine (sprintf "Testing with prevGain: %A, gain: %A, prevSL: %A, expectedSL: %A. actual SL: %A" previousGain gain previousSL expectedSL actualSL)
            actualSL |> should equal expectedSL
        )

    [<Theory(DisplayName = "no prevGain or prevSL ")>]
    [<InlineData( 0)>] // gain 0
    [<InlineData(-1)>] // -ve gain below SL trigger level (currently 0.11 * leverage)
    [<InlineData(0.549)>] // +ve gain below SL trigger level (currently 0.11 * leverage)
    member __.``calculateStopLoss returns minStopLoss`` (gainWithLeverage: float) =
        let previousGain = None
        let previousSL = None
        let leverage = 5m
        let position = mkPosition leverage previousGain previousSL

        let expectedMinSL = Some <| FuturesPositionAnalyser.minStopLoss * leverage

        let gainOpt = Some (decimal gainWithLeverage)
        let newSL = Strategies.FuturesPositionAnalyser.calculateStopLoss position gainOpt
        newSL |> should equal expectedMinSL

    [<Theory>]
    // prevSL, prevGain, gain 0
    [<InlineData(0, 0   , 0   )>] 
    [<InlineData(0, 0.56, 0.57)>]
    [<InlineData(1, 2.5 , 3   )>]
    [<InlineData(2, 3.5 , 4   )>]
    [<InlineData(3, 4.5 , 5   )>]
    member __.``calculateStopLoss always trails up`` (prevSLWithLeverage: float, prevGainWithLeverage: float, gainWithLeverage: float) =
        let previousGain = Some <| decimal prevGainWithLeverage
        let previousSL = Some <| decimal  prevSLWithLeverage
        let leverage = 5m
        let position = mkPosition leverage previousGain previousSL

        let gainOpt = Some (decimal gainWithLeverage)
        let newSL = Strategies.FuturesPositionAnalyser.calculateStopLoss position gainOpt
        newSL.Value |> should greaterThanOrEqualTo previousSL.Value

    [<Theory>]
    [<InlineData( 0,  0    )>] // gain = 0 regardless of prev gain - shouldn't change SL
    [<InlineData(-1,  0    )>] // gain = 0 regardless of prev gain - shouldn't change SL
    [<InlineData( 1,  0    )>] // gain = 0 regardless of prev gain - shouldn't change SL
    [<InlineData(-1,  0.549)>] // +ve gain < SL trigger
    [<InlineData(-1, -0.549)>] // -ve gain < SL trigger
    [<InlineData( 1,  0.9  )>] // gain > SL trigger, but < prev gain
    member __.``calculateStopLoss doesnt change SL`` (prevGainWithLeverage: float, gainWithLeverage: float) =

        let previousSL = Some 1.99m
        let leverage = 5m
        
        let previousGain = Some <| decimal prevGainWithLeverage
        let gain = Some <| decimal gainWithLeverage

        let position = mkPosition leverage previousGain previousSL
        let newSL = Strategies.FuturesPositionAnalyser.calculateStopLoss position gain
        newSL |> should equal previousSL
            
    [<Fact>]
    member __.``Stoploss is calculated correctly without a previous SL`` () =
        let leverage = 5m // 5x 

        let gainValuesWithLeverage = 
            None :: ([
                -0.50m;
                 0.00m;
                 0.11m;
                 0.12m;
                 0.13m;
                 0.14m;
                 0.15m;
                 0.18m;
                 0.19m;
                 0.20m;
                 0.22m;
                 0.25m;
                 0.30m;
                 0.33m;
                 0.40m;
                 0.50m;
                 0.75m;
                 1.00m;
                 1.25m;
                 1.50m;
                 1.75m;
                 2.00m;
                 ] |> List.map (fun g -> g * leverage |> Some))
            |> List.pairwise

        let previousSLValuesWithLeverage =
            (List.replicate 22 None)

        let expectedSLValues =
            [
                -2.50m;
                -2.50m;
                -2.50m;
                -2.50m;
                -2.50m;
                -2.50m;
                -2.50m;
                -2.50m;
                -2.50m;
                -2.50m;
                -2.50m;
                -2.50m;
                 0.33m;
                 0.59m;
                 1.12m;
                 1.80m;
                 3.28m;
                 4.65m;
                 5.97m;
                 7.27m;
                 8.55m;
                 9.82m;
            ]

        runTest leverage gainValuesWithLeverage previousSLValuesWithLeverage expectedSLValues

    [<Fact>]
    member __.``Stoploss is calculated correctly with previous SL`` () =
        let leverage = 5m // 5x 
        
        let gainValuesWithLeverage = 
            None :: ([
                -0.50m;
                 0.00m;
                 0.11m;
                 0.12m;
                 0.13m;
                 0.14m;
                 0.15m;
                 0.18m;
                 0.19m;
                 0.20m;
                 0.22m;
                 0.25m;
                 0.30m;
                 0.33m;
                 0.40m;
                 0.50m;
                 0.75m;
                 1.00m;
                 1.25m;
                 1.50m;
                 1.75m;
                 2.00m;
                 ] |> List.map (fun g -> g * leverage |> Some))
            |> List.pairwise

        let previousSLValuesWithLeverage =
            List.replicate 22 (Some -3m)

        let expectedSLValues =
            [
                -3.00m;
                -3.00m;
                -3.00m;
                -3.00m;
                -3.00m;
                -3.00m;
                -3.00m;
                -3.00m;
                -3.00m;
                -3.00m;
                -3.00m;
                -3.00m;
                 0.33m;
                 0.59m;
                 1.12m;
                 1.80m;
                 3.28m;
                 4.65m;
                 5.97m;
                 7.27m;
                 8.55m;
                 9.82m;
            ]

        runTest leverage gainValuesWithLeverage previousSLValuesWithLeverage expectedSLValues

