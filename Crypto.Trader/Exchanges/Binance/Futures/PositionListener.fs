module Binance.Futures.PositionListener

open System
open Binance.Net.Interfaces.SubClients.Futures
open Binance.Net.Interfaces.SocketSubClient
open Serilog
open FSharpx.Control
open Serilog.Context
open CryptoExchange.Net.Objects
open FSharp.Control
open FsToolkit.ErrorHandling
open Types
open Binance.Futures.Common

let getUsdtPositionsFromBinanceAPI (client: IBinanceClientFuturesUsdt) (symbol: Symbol option) =
    async {
        let! positionResult =
            match symbol with
            | Some (Symbol s) -> client.GetPositionInformationAsync(symbol = s.Replace("_PERP", "")) //ugly hack
            | None   -> client.GetPositionInformationAsync()
            |> Async.AwaitTask
        
        let positions =
            if positionResult.Success
            then
                positionResult.Data
                |> Seq.filter (fun p -> p.PositionAmount <> 0m)
                |> Seq.map (fun p -> {
                        ExchangeId = Types.ExchangeId Binance.Futures.Common.ExchangeId
                        ExchangePosition.EntryPrice = p.EntryPrice
                        Symbol = Symbol p.Symbol
                        Leverage = decimal p.Leverage
                        IsolatedMargin = p.IsolatedMargin
                        LiquidationPrice = p.LiquidationPrice
                        MarginType = FuturesMarginType.FromString <| p.MarginType.ToString() // cross or isolated
                        Side = PositionSide.FromString <| p.PositionSide.ToString() // long or short
                        Amount = p.PositionAmount
                        MarkPrice = p.MarkPrice
                        RealisedPnL = 0M // this is available only in the stream updates :/
                        UnRealisedPnL = p.UnrealizedProfit // this does not consider fees etc
                        // EntryTime we don't actually know - unless we query orders and guess / calculate over time :|
                    })
            else
                Log.Error("Error getting USDT futures positions: {Error}. Symbol (None means all): {Symbol}", positionResult.Error, symbol)
                Seq.empty

        return positions
    }

let getCoinPositionsFromBinanceAPI (client: IBinanceClientFuturesCoin) (symbol: Symbol option) =
    async {
        let! positionResult =
            match symbol with
            | Some (Symbol s) -> client.GetPositionInformationAsync(pair = s.Replace("_PERP", ""))
            | None   -> client.GetPositionInformationAsync()
            |> Async.AwaitTask

        let positions =
            if positionResult.Success
            then
                positionResult.Data
                |> Seq.filter (fun p -> p.PositionAmount <> 0m)
                |> Seq.map (fun p -> {
                        ExchangeId = Types.ExchangeId Binance.Futures.Common.ExchangeId
                        ExchangePosition.EntryPrice = p.EntryPrice
                        Symbol = Symbol p.Symbol
                        Leverage = decimal p.Leverage
                        IsolatedMargin = p.IsolatedMargin
                        LiquidationPrice = p.LiquidationPrice
                        MarginType = FuturesMarginType.FromString <| p.MarginType.ToString() // cross or isolated
                        Side = PositionSide.FromString <| p.PositionSide.ToString() // long or short
                        Amount = p.PositionAmount
                        MarkPrice = p.MarkPrice
                        RealisedPnL = 0M // this is available only in the stream updates :/
                        UnRealisedPnL = p.UnrealizedProfit // this does not consider fees etc
                        // EntryTime we don't actually know - unless we query orders and guess / calculate over time :|
                    })
            else
                Log.Error("Error getting COIN-M futures positions: {Error}. Symbol (None means all): {Symbol}", positionResult.Error, symbol)
                Seq.empty

        return positions
    }

let private keepUserStreamSubscriptionAlive (clientFutures: IBinanceClientFutures) (listenKey: string) (subscription: CallResult<CryptoExchange.Net.Sockets.UpdateSubscription>) = 
    if subscription.Success
    then
        Log.Information ("Binance UserData Socket subscription successful")

        let mutable keepAliveSuccess = true
        repeatEveryIntervalWhile (fun () -> keepAliveSuccess) (TimeSpan.FromMinutes(30.0)) (
            fun _ -> 
                Log.Information ("Sending user steam keep alive to Binance")
                clientFutures.UserStream.KeepAliveUserStreamAsync(listenKey)
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

let private subscribeToUserStream (tradeAgent: MailboxProcessor<PositionCommand>) (clientFutures: IBinanceClientFutures) (socketClientFutures: IBinanceSocketClientFutures) (listenKey: string) =
       socketClientFutures.SubscribeToUserDataUpdates (
           listenKey,
           onOrderUpdate = null,
           onLeverageUpdate = null,
           onAccountUpdate = (fun accUpdate -> 
                                let positions =
                                    accUpdate.UpdateData.Positions
                                    |> Seq.map (fun p -> {
                                        ExchangeId = Types.ExchangeId Binance.Futures.Common.ExchangeId
                                        ExchangePosition.Amount = p.PositionAmount
                                        Leverage = 0M // TODO fetch or let the higher level flow determine it
                                        Side = PositionSide.FromString <| p.PositionSide.ToString()
                                        Symbol = Symbol p.Symbol
                                        EntryPrice = p.EntryPrice
                                        MarkPrice = 0M // not available here
                                        MarginType = FuturesMarginType.FromString <| p.MarginType.ToString()
                                        RealisedPnL = p.RealizedPnL
                                        UnRealisedPnL = p.UnrealizedPnl
                                        IsolatedMargin = p.IsolatedWallet
                                        LiquidationPrice = 0M // not available here
                                    })
                                tradeAgent.Post (FuturesPositionUpdate (Types.ExchangeId Binance.Futures.Common.ExchangeId, positions))
                             ),
           onMarginUpdate = null,
           onListenKeyExpired = null
       )
    |> keepUserStreamSubscriptionAlive clientFutures listenKey

let private listenToFuturesPriceTickerForSymbol (tradeAgent: MailboxProcessor<PositionCommand>) (socketClientFutures: IBinanceSocketClientFutures) (Symbol symbol: Symbol) =
    let subscription = 
        socketClientFutures.SubscribeToBookTickerUpdates (
            symbol,
            onMessage = (fun msg -> 
                let orderBookTickerInfo = {
                    OrderBookTickerInfo.AskPrice = msg.BestAskPrice
                    BidPrice = msg.BestBidPrice
                    Symbol = Symbol msg.Symbol
                    AskQty = msg.BestAskQuantity
                    BidQty = msg.BestBidQuantity
                }
                tradeAgent.Post (FuturesBookPrice (Types.ExchangeId Binance.Futures.Common.ExchangeId, orderBookTickerInfo)))
        )
    Log.Information("Subscribing to futures price ticker for {Symbol}. Success: {Result}. Error: {Error}", symbol, subscription.Success, subscription.Error)
    subscription.Success

let private listenFutures (tradeAgent: MailboxProcessor<PositionCommand>) (clientFutures: IBinanceClientFutures) (socketClientFutures: IBinanceSocketClientFutures) (symbols: Symbol seq) =
    let listenKeyResponse = clientFutures.UserStream.StartUserStream()
    if not listenKeyResponse.Success
    then failwith (sprintf "Error starting user stream: [%d] - %s" (listenKeyResponse.Error.Code.GetValueOrDefault()) listenKeyResponse.Error.Message)
    else
        subscribeToUserStream tradeAgent clientFutures socketClientFutures listenKeyResponse.Data

        symbols
        |> Seq.map (listenToFuturesPriceTickerForSymbol tradeAgent socketClientFutures)
        |> Seq.reduce ((&&))

let trackPositions (tradeAgent: MailboxProcessor<PositionCommand>)  =
    use _x = LogContext.PushProperty ("Futures", true)

    let client = getBaseClient ()
    let socketClient = getSocketClient ()

    Log.Information "Starting socket client for Binance futures user data stream"
    
    let usdtSymbols = Common.usdtSymbols.Keys
    let coinMSymbols = Common.coinMSymbols.Keys

    let listenCOINM = 
        if not <| Seq.isEmpty coinMSymbols
        then listenFutures tradeAgent client.FuturesCoin socketClient.FuturesCoin coinMSymbols
        else true

    let listenUSDT = 
        if not <| Seq.isEmpty usdtSymbols
        then listenFutures tradeAgent client.FuturesUsdt socketClient.FuturesUsdt usdtSymbols
        else true

    listenUSDT && listenCOINM
