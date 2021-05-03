module Binance.Spot.TradeStatusListener

open System
open Binance.Net
open Serilog
open DbTypes
open CryptoExchange.Net.Authentication
open CryptoExchange.Net.Logging
open Serilog.Context
open Types
open FSharpx.Control
open Binance.Net.Objects.Spot.UserStream
open Binance.Net.Objects.Spot
open Binance.ApiTypes

type private TradeAgentCommand = 
    | UpdateOrder of BinanceStreamOrderUpdate

let parseOrderIds (clientOrderId: string) = 
    let idParts = clientOrderId.Split([|"__"|], StringSplitOptions.RemoveEmptyEntries)
    if idParts.Length = 2
    then
        let (foundSignalId, sId) = Int64.TryParse (idParts.[0] |> string)
        let (foundInternalOrderId, oId) = Int64.TryParse (idParts.[1] |> string)
        
        let signalId = if foundSignalId then Some sId else None
        let internalOrderId = if foundInternalOrderId then Some oId else None

        signalId, internalOrderId
    else
        None, None

let private updateOrder' getExchangeOrder (saveOrder: ExchangeOrder -> TradeMode -> Async<Result<int64, exn>>) (t: BinanceStreamOrderUpdate) =
    async {
        let toUpper (s: string) = s.ToUpper()

        let parsedSignalId, parsedInternalOrderId = parseOrderIds t.OriginalClientOrderId

        match parsedSignalId, parsedInternalOrderId with
        | Some signalId, Some internalOrderId -> 
            use _ = LogContext.PushProperty("InternalOrderId", internalOrderId)
            use _ = LogContext.PushProperty("SignalId", signalId)

            // Binance doesn't give us the full fees in each update. Only the incremental fees is returned.
            // check the db if we already know about this order
            let! previousOrderUpdate = getExchangeOrder internalOrderId
            let commissionSoFar =
                previousOrderUpdate 
                |> Option.map (fun o -> if o.FeeCurrency = t.CommissionAsset then o.FeeAmount else 0M)
                |> Option.defaultValue 0M
            
            if previousOrderUpdate.IsNone then
                Log.Warning("Got an order update from Binance. for an unknown order. ExchangeOrderId: {ExchangeOrderId}. ExchangeClientOrderId: {ExchangeClientOrderId}",
                    t.OrderId, t.ClientOrderId
                )

            let exo = {
                // this may be zero for orders placed directly on the exchange
                ExchangeOrder.Id = previousOrderUpdate |> Option.map (fun o -> o.Id) |> Option.defaultValue 0L 
                ExchangeId = Binance.ExchangeId
                OrderSide = t.Side |> string |> toUpper
                ExchangeOrderId = string t.OrderId
                ExchangeOrderIdSecondary = t.ClientOrderId
                SignalId = signalId
                Status = string t.Status |> toUpper
                StatusReason = "Status update"
                Symbol = t.Symbol
                Price = t.Price
                ExecutedPrice = t.Price // TODO is this the avg price of all filled trades so far?
                OriginalQty = t.Quantity
                ExecutedQty = t.QuantityFilled  // t.AccumulatedQuantityOfFilledTrades
                FeeAmount = t.Commission + commissionSoFar
                FeeCurrency = t.CommissionAsset
                LastTradeId = t.TradeId
                CreatedTime = t.CreateTime.ToUniversalTime()
                UpdatedTime = DateTime.UtcNow
            }
            Log.Information ("Saving status update ({Status}) for {OrderSide} order: {Symbol}. Signal: {SignalId}. Avg price? : {AvgPrice}. RejectReason: {RejectReason}", 
                exo.Status,
                exo.OrderSide,
                exo.Symbol,
                exo.SignalId,
                t.Price,
                t.RejectReason
            )

            let! saveResult = saveOrder exo SPOT
            match saveResult with
            | Result.Error err ->
                Log.Error("Error saving spot trade update from Binance websocket: {Error}", err)
            | _ -> ()

        | _ -> 
            Log.Information("Could not parse internal order id and signal id from exchange clientOrderId: {ClientOrderID}", t.OriginalClientOrderId)
    }

let private mkAgent getExchangeOrder saveOrder = 
    MailboxProcessor<TradeAgentCommand>.Start (fun inbox ->
        let rec messageLoop() = async {
            let! msg = inbox.Receive()

            try
                use _ = LogContext.PushProperty("ExchangeId", Binance.ExchangeId)

                match msg with
                | UpdateOrder d -> 
                    let! _ = (updateOrder' getExchangeOrder saveOrder d)
                    ()
 
            with e ->
                Log.Error (e, "Error handling Binance update order command: {TradeAgentCommand}, continuing...", msg)

            return! messageLoop()
        }
        messageLoop()
    )

let private getSocketClient (apiKey: BinanceApiKey) =
    new BinanceSocketClient (
        BinanceSocketClientOptions (
            ApiCredentials = new ApiCredentials(apiKey.Key, apiKey.Secret),
            LogVerbosity = LogVerbosity.Debug,
            AutoReconnect = true
        ))
let private getClient (apiKey: BinanceApiKey) = 
    new BinanceClient (
        BinanceClientOptions (
            ApiCredentials = new ApiCredentials(apiKey.Key, apiKey.Secret),
            LogVerbosity = LogVerbosity.Debug
        ))

#nowarn "40" // warning about recursive definitions

// ugly mutables: but needed
let mutable private client: BinanceClient = null
let mutable private socketClient: BinanceSocketClient = null
let mutable private started: bool = false

// Start listener or keep listening if already started
let rec listen getExchangeOrder (saveOrder: ExchangeOrder -> TradeMode -> Async<Result<int64, exn>>) (apiKey: BinanceApiKey) =
    Log.Information "Starting socket client for Binance user data stream"

    // for now disabling full update
    // TradeStatusUpdater.updateOrderStatuses () |> Async.Start // start a background process to update any missed order statuses

    let agent = mkAgent getExchangeOrder saveOrder

    if client = null || not started then
        client <- getClient apiKey
        socketClient <- getSocketClient apiKey

        let listenKeyResponse = client.Spot.UserStream.StartUserStream()
        if not listenKeyResponse.Success
        then failwith (sprintf "Error starting user stream: [%d] - %s" (listenKeyResponse.Error.Code.GetValueOrDefault()) listenKeyResponse.Error.Message)
        else
            let subscription = 
                socketClient.Spot.SubscribeToUserDataUpdates (
                    listenKeyResponse.Data,
                    onOrderUpdateMessage = (fun d -> agent.Post <| UpdateOrder d),
                    onOcoOrderUpdateMessage = null,
                    onAccountPositionMessage = null,
                    onAccountBalanceUpdate = null
                )
            if subscription.Success
            then
                Log.Information "Binance Socket subscription successful"

                let mutable keepAliveSuccess = true
                repeatEveryIntervalWhile (fun () -> keepAliveSuccess) (TimeSpan.FromMinutes(30.0)) (
                    fun _ -> 
                        Log.Information ("Sending user steam keep alive to Binance")
                        client.Spot.UserStream.KeepAliveUserStreamAsync(listenKeyResponse.Data)
                        |> Async.AwaitTask
                        |> Async.map (fun r ->
                            if not r.Success then
                                Log.Warning("Error sending user stream keep alive to Binance. {ResponseStatusCode}: {Error}",
                                    r.ResponseStatusCode, r.Error)
                                keepAliveSuccess <- false
                            )
                ) "Binance-UserStream-KeepAlive" |> Async.Start

                started <- true
            else
                Log.Error (sprintf "Binance subscription error %d: %s" (subscription.Error.Code.GetValueOrDefault()) subscription.Error.Message)
        
            subscription.Success
    else
        true // already started and listening