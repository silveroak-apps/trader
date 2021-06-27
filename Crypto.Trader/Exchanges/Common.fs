module Exchanges.Common
open System
open System.Net.Http
open Serilog


type ApiKey = {
    Key: string
    Secret: string
}

type PushOverMessage = {
    Message: string
}

let getFuturesMode (Symbol symbol) =
    if symbol.EndsWith("usdt", StringComparison.OrdinalIgnoreCase)
    then FuturesMarginMode.USDT
    else COINM

let cfg = appConfig.GetSection "PushOver"
let private pushoverUrl = cfg.GetSection "Url"
let private pushoverUserKey = cfg.Item "UserKey"
let private pushoverAppKey = cfg.Item "AppKey"
let private pushoverHttpClient = new HttpClient()

let raisePushOverAlert (message: PushOverMessage) =
    async {
        match pushoverUrl.Value with
        | pushoverUrl when pushoverUrl.Length > 0 ->
            Log.Information("Pushing alret to pushover {Message}", message)
            try
                let pushoverUri = sprintf "%s/?user=%s&token=%s/message=%s" pushoverUrl pushoverUserKey pushoverAppKey (string message)
                let! response =
                    pushoverHttpClient.GetAsync(pushoverUri)
                    |> Async.AwaitTask
                
                if response.IsSuccessStatusCode
                then 
                    return Ok message
                else 
                    return (Error <| sprintf "Error raising alert to pushover: %A - %A - %s" message response.StatusCode response.ReasonPhrase)
            with ex -> 
                Log.Error (ex, "Error raising pushover alert: {PushOverMessage}", message)
                return Error <| sprintf "Error sending pushover message"
        | _ ->
            return Error ("Not raising any alret as PushOver url is empty")
    }
    