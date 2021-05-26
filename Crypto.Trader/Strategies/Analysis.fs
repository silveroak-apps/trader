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

    OriginalClose: decimal
}

let heikenAshi (candles: KLine seq)  =
    let toHA ((previous, current): KLine * KLine) =
        {
            HeikenAshi.OpenTime = current.OpenTime
            IntervalMinutes = current.IntervalMinutes
            Open = (previous.Open + previous.Close) / 2M
            Close = (current.Open + current.Close + current.High + current.Low) / 4M
            High = current.High
            Low = current.Low
            Volume = current.Volume
            Symbol = current.Symbol

            OriginalClose = current.Close
        }
    candles
    |> Seq.pairwise
    |> Seq.map toHA
