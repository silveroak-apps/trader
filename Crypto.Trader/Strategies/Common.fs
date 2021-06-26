module Strategies.Common
open System.Net.Http
open System.Text.Json
open Types
open Serilog

let knownMarketDataProviders = dict [
    ( ExchangeId Binance.Futures.Common.ExchangeId, Binance.Futures.Market.getMarketDataProvider () )
    ( ExchangeId Bybit.Futures.Common.ExchangeId,  Bybit.Futures.Market.getMarketDataProvider ()   )
]

type MarketEvent = {
    Name: string
    Price: decimal
    Symbol: string
    Market: string
    TimeFrame: int
    Exchange: string
    Category: string
    Contracts: decimal
}

let private marketEventUrl = appConfig.GetSection "MarketEventUrl"
let private marketEventApiKey = appConfig.GetSection "MarketEventApiKey"
let private marketEventHttpClient = new HttpClient()
let private jsonOptions = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let raiseMarketEvent (marketEvent: MarketEvent) =
    async {
        match marketEventUrl.Value with
        | marketEvtUrl when marketEvtUrl.Length > 0 ->
            
            if not <| marketEventHttpClient.DefaultRequestHeaders.Contains("x-api-key")
            then marketEventHttpClient.DefaultRequestHeaders.Add("x-api-key", marketEventApiKey.Value)
           
            let json = JsonSerializer.Serialize(marketEvent, jsonOptions)
            let content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            
            try
                let! response =
                    marketEventHttpClient.PostAsync(marketEvtUrl, content) 
                    |> Async.AwaitTask
                
                if response.IsSuccessStatusCode
                then 
                    return Ok marketEvent
                else 
                    return (Error <| sprintf "Error raising a market event to close a position: %A - %A - %s" marketEvent response.StatusCode response.ReasonPhrase)
            with ex -> 
                Log.Error (ex, "Error raising market event: {MarketEvent}", marketEvent)
                return Error <| sprintf "Error raising a market event to close a position: %A - %A" marketEvent ex
        | _ ->
            return Error ("Position analyser not raising any events because MarketEventUrl is not configured")
    }

// TODO move slippage to db config
let tradePriceSlippageAllowance = 0.3M // 0.08% change in price is the max we tolerate before placing a trade

let futuresTradeFeesPercentFor (ExchangeId exchangeId) = 
    match exchangeId with
    | v when v = Binance.Futures.Common.ExchangeId -> 0.05M // Futures trade fees (without leverage in each direction) assume the worst: current market order fees for Binance is 0.04% (https://www.binance.com/en/support/articles/360033544231)
    | v when v = Bybit.Futures.Common.ExchangeId -> 0M // We get maker fees, which is actually -ve (i.e in our favour) - but let's be conservative here for now
    | _ -> 0.1M