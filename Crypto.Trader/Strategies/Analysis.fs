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
    let toFirstHA (current: KLine) =
        let closePrice = (current.Open + current.Close + current.High + current.Low) / 4M
        let openPrice = (current.Open + current.Close) / 2M
        let ha = {
            HeikenAshi.OpenTime = current.OpenTime
            IntervalMinutes = current.IntervalMinutes
            Close = closePrice
            Open = openPrice
            High = [current.High; openPrice; closePrice] |> List.max
            Low = [current.Low; openPrice; closePrice] |> List.min
            Volume = current.Volume
            Symbol = current.Symbol

            Original = current
        }
        // printfn "Current Open - %f Open - %f, Current Close - %f, Close - %f" current.Open r.Open current.Close r.Close
        ha
        
    let generateHA ((previous, klines, index): HeikenAshi * KLine array * int) =
        if index >= Array.length klines
        then None
        else
            let currentKLine = klines.[index]
            let closePrice = (currentKLine.Open + currentKLine.Close + currentKLine.High + currentKLine.Low) / 4M
            let openPrice = (previous.Open + previous.Close) / 2M
            let newHA = {
                HeikenAshi.OpenTime = currentKLine.OpenTime
                IntervalMinutes = currentKLine.IntervalMinutes
                Close = closePrice
                Open = openPrice
                High = [currentKLine.High; openPrice; closePrice] |> List.max
                Low = [currentKLine.Low; openPrice; closePrice] |> List.min
                Volume = currentKLine.Volume
                Symbol = currentKLine.Symbol

                Original = currentKLine
            }
            let newState = (newHA, klines, index + 1)
            Some (newHA, newState)

    if Seq.length candles > 0
    then
        let candlesArray = candles |> Seq.toArray
        let firstHA = toFirstHA <| Seq.head candles
        let initialState = (firstHA, candlesArray, 0)
        Seq.unfold generateHA initialState
    else Seq.empty
