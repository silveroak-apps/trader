module Strategies.Analysis
open AnalysisTypes
open System

type HeikenAshi = {
    Symbol: Symbol
    IntervalMinutes: int
    Open: decimal
    Close: decimal
    High: decimal
    Low: decimal
    Volume: decimal
    OpenTime: DateTimeOffset

    Original: KLine
}

(*
From: https://school.stockcharts.com/doku.php?id=chart_analysis:heikin_ashi
1. The Heikin-Ashi Close is simply an average of the open, 
high, low and close for the current period. 

<b>HA-Close = (Open(0) + High(0) + Low(0) + Close(0)) / 4</b>

2. The Heikin-Ashi Open is the average of the prior Heikin-Ashi 
candlestick open plus the close of the prior Heikin-Ashi candlestick. 

<b>HA-Open = (HA-Open(-1) + HA-Close(-1)) / 2</b> 

3. The Heikin-Ashi High is the maximum of three data points: 
the current period's high, the current Heikin-Ashi 
candlestick open or the current Heikin-Ashi candlestick close. 

<b>HA-High = Maximum of the High(0), HA-Open(0) or HA-Close(0) </b>

4. The Heikin-Ashi low is the minimum of three data points: 
the current period's low, the current Heikin-Ashi 
candlestick open or the current Heikin-Ashi candlestick close.

<b>HA-Low = Minimum of the Low(0), HA-Open(0) or HA-Close(0) </b>
*)
let heikenAshi (candles: KLine seq)  =
    let toHA1 (current: KLine) =
        {
            HeikenAshi.OpenTime = current.OpenTime
            IntervalMinutes = current.IntervalMinutes
            Close = (current.Open + current.Close + current.High + current.Low) / 4M // Calc from Candles
            Open = (current.Open + current.Close) / 2M
            High = current.High
            Low = current.Low
            Volume = current.Volume
            Symbol = current.Symbol

            Original = current
        }
    let toHA2 ((previousHA, currentHA): HeikenAshi * HeikenAshi) =
        let newOpen = (previousHA.Open + previousHA.Close) / 2M
        {
            currentHA with
                Open = newOpen
                High = [ currentHA.Original.High; newOpen; currentHA.Close ] |> List.max
                Low =  [ currentHA.Original.Low;  newOpen; currentHA.Close ] |> List.min
        }

    let haCloseValues =
        candles
        |> Seq.map (fun current -> 
                (current.Open + current.Close + current.High + current.Low) / 4M
            )

    // let haOpenValues =
    //     let firstValue = candles |> Seq.tryHead |> Option.map (fun c -> (c.Open + c.Close) / 2m)
    //     // Seq.

    candles
    |> Seq.map toHA1
    |> Seq.pairwise
    |> Seq.map toHA2
