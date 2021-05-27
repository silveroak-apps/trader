module Strategies.Common
open System.Net.Http
open System.Text.Json

let knownMarketDataProviders = dict [
    ( Bybit.Futures.Market.ExchangeId, Bybit.Futures.Market.getMarketDataProvider () )
]

type MarketEvent = {
    Name: string
    Price: decimal
    Symbol: string
    Market: string
    TimeFrame: string
    Exchange: string
    Category: string
    Contracts: decimal
}

let private marketEventUrl = appConfig.GetSection "MarketEventUrl"
let private marketEventApiKey = appConfig.GetSection "MarketEventApiKey"
let private marketEventHttpClient = new HttpClient()
let private jsonOptions = new JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let raiseMarketEvent (marketEvent: MarketEvent) =
    async {
        match marketEventUrl.Value with
        | marketEvtUrl when marketEvtUrl.Length > 0 ->
            
            if not <| marketEventHttpClient.DefaultRequestHeaders.Contains("x-api-key")
            then marketEventHttpClient.DefaultRequestHeaders.Add("x-api-key", marketEventApiKey.Value)
           
            let json = JsonSerializer.Serialize(marketEvent, jsonOptions)
            let content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            let! response =
                marketEventHttpClient.PostAsync(marketEvtUrl, content) 
                |> Async.AwaitTask
            
            if response.IsSuccessStatusCode
            then 
                return Ok marketEvent
            else 
                return (Error <| sprintf "Error raising a market event to close the long: %A - %s" response.StatusCode response.ReasonPhrase)
        | _ ->
            return Error ("Position analyser not raising any events because MarketEventUrl is not configured")
    }
