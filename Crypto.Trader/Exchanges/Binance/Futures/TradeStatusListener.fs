module Binance.Futures.TradeStatusListener

open System
open Binance.Net
open Serilog
open DbTypes
open Serilog.Context
open Types
open FSharpx.Control
open Binance.Net.Objects.Futures.UserStream
open Binance.Net.Interfaces.SubClients.Futures
open Binance.Net.Interfaces.SocketSubClient

type private TradeAgentCommand = 
    | UpdateOrder of BinanceFuturesStreamOrderUpdate

let private updateOrder' 
        (getExchangeOrder: int64 -> Async<option<ExchangeOrder>>)
        saveOrder (u: BinanceFuturesStreamOrderUpdate) =
    let toUpper (s: string) = s.ToUpper()

    let d = u.UpdateData
    let parsedSignalId, parsedInternalOrderId = Binance.Spot.TradeStatusListener.parseOrderIds d.ClientOrderId

    async {

        match parsedSignalId, parsedInternalOrderId with
        | Some signalId, Some internalOrderId -> 
            use _ = LogContext.PushProperty("InternalOrderId", internalOrderId)    
            use _ = LogContext.PushProperty("SignalId", signalId)

            // Binance doesn't give us the full fees in each update. Only the incremental fees is returned.
            // check the db if we already know about this order
            let! previousOrderUpdate = getExchangeOrder internalOrderId
            let commissionSoFar =
                previousOrderUpdate 
                |> Option.map (fun o -> if o.FeeCurrency = d.CommissionAsset then o.FeeAmount else 0M)
                |> Option.defaultValue 0M
            
            if previousOrderUpdate.IsNone then
                Log.Warning("Got an order update from Binance, for an unknown order. ExchangeOrderId: {ExchangeOrderId}. ExchangeClientOrderId: {ExchangeClientOrderId}",
                    d.OrderId, d.ClientOrderId
                )

            let exo = {
                // this may be zero for orders placed directly on the exchange
                ExchangeOrder.Id = previousOrderUpdate |> Option.map (fun o -> o.Id) |> Option.defaultValue 0L 
                ExchangeId = Trade.ExchangeId
                OrderSide = d.Side |> string |> toUpper
                ExchangeOrderId = string d.OrderId
                ExchangeOrderIdSecondary = d.ClientOrderId
                SignalId = signalId
                Status = string d.Status |> toUpper
                StatusReason = "Status update"
                Symbol = d.Symbol
                Price = d.Price
                ExecutedPrice = d.Price // TODO is this the avg price of all filled trades so far?
                OriginalQty = d.Quantity
                ExecutedQty = d.AccumulatedQuantityOfFilledTrades
                FeeAmount = d.Commission + commissionSoFar
                FeeCurrency = d.CommissionAsset
                LastTradeId = d.TradeId
                CreatedTime = d.CreateTime.ToUniversalTime()
                UpdatedTime = DateTime.UtcNow
            }
            Log.Information ("Saving status update ({Status}) for {OrderSide} order: {Symbol}. Signal: {SignalId}. Avg price? : {AvgPrice}.", 
                exo.Status,
                exo.OrderSide,
                exo.Symbol,
                exo.SignalId,
                d.Price
            )

            let! exoId = saveOrder exo FUTURES
            return (Some exoId)
        | _ ->
            Log.Warning("Got a status update from the websocket for an order - could not parse signal id from clientOrderId: {ClientOrderId}. Exchange's order: {@Order}", d.ClientOrderId, d)
            return None  
    }

let private mkAgent getExchangeOrder saveOrder = 
    MailboxProcessor<TradeAgentCommand>.Start (fun inbox ->
        let rec messageLoop() = async {
            let! msg = inbox.Receive()

            try
                use _ = LogContext.PushProperty("ExchangeId", Trade.ExchangeId)

                match msg with
                | UpdateOrder d -> 
                    do! updateOrder' getExchangeOrder saveOrder d |> Async.Ignore
                
            with e ->
                Log.Error (e, "Error handling Binance futures update order command: {TradeAgentCommand}, continuing...", msg)

            return! messageLoop()
        }
        messageLoop()
    )

#nowarn "40" // warning about recursive definitions

let rec private listenFutures getExchangeOrder saveOrder (clientFutures: IBinanceClientFutures) (socketClientFutures: IBinanceSocketClientFutures) =
    let listenKeyResponse = clientFutures.UserStream.StartUserStream()
    if not listenKeyResponse.Success
    then failwith (sprintf "Error starting user stream: [%d] - %s" (listenKeyResponse.Error.Code.GetValueOrDefault()) listenKeyResponse.Error.Message)
    else
        let agent = mkAgent getExchangeOrder saveOrder

        let subscription = 
            socketClientFutures.SubscribeToUserDataUpdates (
                listenKeyResponse.Data,
                onOrderUpdate = (fun d -> agent.Post <| UpdateOrder d),
                onLeverageUpdate = null,
                onAccountUpdate = null,
                onMarginUpdate = null,
                onListenKeyExpired = null // TODO do we need to do something here?
            )
        if subscription.Success
        then
            Log.Information "Binance Socket subscription successful"

            let mutable keepAliveSuccess = true
            repeatEveryIntervalWhile (fun () -> keepAliveSuccess) (TimeSpan.FromMinutes(30.0)) (
                fun _ -> 
                    Log.Information ("Sending user steam keep alive to Binance")
                    clientFutures.UserStream.KeepAliveUserStreamAsync(listenKeyResponse.Data)
                    |> Async.AwaitTask
                    |> Async.map (fun r ->
                        if not r.Success then
                            Log.Warning("Error sending user stream keep alive to Binance. {ResponseStatusCode}: {Error}",
                                r.ResponseStatusCode, r.Error)
                            keepAliveSuccess <- false
                        )
            ) "Binance-UserStream-KeepAlive" |> Async.Start
        else
            Log.Error (sprintf "Binance subscription error %d: %s" (subscription.Error.Code.GetValueOrDefault()) subscription.Error.Message)
    
        subscription.Success

// ugly mutables 
let mutable private client: BinanceClient = null
let mutable private socketClient: BinanceSocketClient = null
let mutable private started: bool = false

// Start listener or keep listening if already started
let listen getExchangeOrder saveOrder =
    use _x = LogContext.PushProperty ("Futures", true)
    Log.Information "Starting socket client for Binance user data stream"

    if client = null then
        client <- Trade.getBaseClient ()
        socketClient <- Trade.getSocketClient ()

    started <- 
        started || 
            listenFutures getExchangeOrder saveOrder client.FuturesCoin socketClient.FuturesCoin &&
            listenFutures getExchangeOrder saveOrder client.FuturesUsdt socketClient.FuturesUsdt
    started
