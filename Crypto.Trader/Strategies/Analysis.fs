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
        let r = {
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
        printfn "Current Open - %f Open - %f, Current Close - %f, Close - %f" current.Open r.Open current.Close r.Close
        r
        

    let toHA (current, previous) =
        (current, previous)
        |>  Seq.unfold (fun k ->
            Some(k , 
                let newOpen = ((fst k).Open + (fst k).Close) / 2M
                previous, {
                    current with
                        Open = newOpen
                        High = [ (snd k).Original.High; newOpen; (snd k).Close ] |> List.max
                        Low =  [ (snd k).Original.Low;  newOpen; (snd k).Close ] |> List.min
                }))
            

    candles
    |> Seq.map toHA1
    |> Seq.pairwise
    |> Seq.map toHA
    |> Seq.head
    |> Seq.map (fun (_, c) -> c)

