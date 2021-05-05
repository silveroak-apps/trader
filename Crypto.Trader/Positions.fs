module Positions

open Binance.Net
open System
open Binance.Net.Interfaces.SubClients.Futures
open Binance.Net.Interfaces.SocketSubClient
open Serilog
open FSharpx.Control
open Serilog.Context
open Binance.Net.Objects.Futures.UserStream
open System.Collections.Generic
open CryptoExchange.Net.Objects
open Binance.Net.Objects.Futures.MarketStream
open System.Collections.Concurrent
open FSharp.Control
open System.Net.Http
open System.Text.Json
open Trade
open DbTypes
open FsToolkit.ErrorHandling

type Position = {
    EntryPrice: decimal
    Symbol: Symbol
    IsolatedMargin: decimal
    Leverage: int
    LiquidationPrice: decimal
    MarginType: Enums.FuturesMarginType
    PositionSide: Enums.PositionSide
    PositionAmount: decimal // Total size of the position (after including leverage)
    MarkPrice: decimal
    RealisedPnl: decimal option // we get this from the exchange (sometimes)
    UnrealisedPnl: decimal // we get this from the exchange
    CalculatedPnl: decimal option // we calculate this when there is a ticker update
    CalculatedPnlPercent: decimal option // calculated on ticker price update
    StoplossPnlPercentValue: decimal option // Stop loss expressed in terms of Pnl percent (not price)
    MaxQuantity: decimal //not sure what this is
    IsStoppedOut: bool
}

type MarketEvent = {
    Name: string
    Price: decimal
    Symbol: string
    Market: string
    TimeFrame: string
    Exchange: string
    Category: string
}

let private marketEventCfg = appConfig.GetSection "MarketEventUrl"
let private marketEventApiKey = appConfig.GetSection "MarketEventApiKey"
let private marketEventHttpClient = new HttpClient()
let private jsonOptions = new JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

let private tradeMode = appConfig.GetSection "TradeMode"

type ContractDetails = {
    Multiplier: int // i.e if it is contracts, how many USD is 1 contract
}

// TODO move this to config / db?
// UnitName is what the 'quantity' refers to in the API calls.
// From there we derive a USD value - if it is USDT, we leave it as is.
// If it is 'CONT' or contracts, we can convert to USD
let usdtSymbols  = 
    dict [ 
        ("BNBUSDT", { Multiplier = 1 })
        ("BTCUSDT", { Multiplier = 1 })
        ("ETHUSDT", { Multiplier = 1 })
        ("ADAUSDT", { Multiplier = 1 })
        ("DOTUSDT", { Multiplier = 1 })
    ]

let coinMSymbols =
    dict [ 
        ("BNBUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
        ("BTCUSD_PERP", { Multiplier = 100 }) // 1 cont = 100 USD
        ("ETHUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
        ("ADAUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
        ("DOTUSD_PERP", { Multiplier = 10 })  // 1 cont = 10 USD
    ]

let placeOrders (signalCommand: FuturesSignalCommandView) : Async<Result<unit, exn>> =
    asyncResult {
        //TODO fix some hardcoded values
        let maxSlippage = 0.15m // not really used at the moment
        let maxAttempts = 3
        let attemptCount = 1
        let saveOrder o = 
            asyncResult {
                let! id = Db.saveOrder o TradeMode.FUTURES
                return { o with Id = id }
            }
        let! exchange = (Futures.getExchange signalCommand.ExchangeId |> Result.mapError exn)
        let! _ = Futures.executeOrdersForCommand exchange saveOrder maxSlippage [] maxAttempts attemptCount signalCommand
        return ()
    }

let private closeSignal (position: Position) (price: BinanceFuturesStreamBookPrice) =
    let (Symbol symbol) = position.Symbol
    let strategy = sprintf "sell_analyser_close_%s_trailing_stop_loss_%s" symbol (position.PositionSide.ToString().ToLower())

    match tradeMode.Value with
    | "MarketEvent" ->
        
        let marketEvent = {
            MarketEvent.Name = strategy
            Price = price.BestBidPrice
            Symbol = symbol.ToUpperInvariant()
            Market = "" // hardcode for now - no relevant for now
            TimeFrame = "1m" // hardcode for now
            Exchange = "Binance"
            Category = "Stoploss"
        }
        async {
            if not <| marketEventHttpClient.DefaultRequestHeaders.Contains("x-api-key")
            then marketEventHttpClient.DefaultRequestHeaders.Add("x-api-key", marketEventApiKey.Value)
           
            let json = JsonSerializer.Serialize(marketEvent, jsonOptions)
            let content = new StringContent(json, Text.Encoding.UTF8, "application/json")

            let! response = 
                marketEventHttpClient.PostAsync(marketEventCfg.Value, content) 
                |> Async.AwaitTask
            if response.IsSuccessStatusCode
            then Log.Information("Raised a market event to close the long: {@MarketEvent}", marketEvent)
            else Log.Error ("Error raising a market event to close the long: {Status} - {ErrorReason}", response.StatusCode, response.ReasonPhrase)
            
            return ()
        }

    | _ ->
        // place trade directly on exchange! works for manual trade takeover without any other dependencies
        asyncResult {
            let cmd = {
                Id = 1L // does not matter for this
                SignalId = 0L // does not matter for this : but will be good to pull in ClientOrderId later
                FuturesSignalCommandView.Action = "CLOSE"
                PositionType = position.PositionSide.ToString().ToUpper()
                ExchangeId = Binance.Futures.Trade.ExchangeId
                ActionDateTime = DateTime.Now
                Price = if position.PositionSide = Enums.PositionSide.Long then price.BestAskPrice else price.BestBidPrice
                Quantity = Math.Abs position.PositionAmount
                Symbol = symbol
                RequestDateTime = DateTime.Now
                Leverage = position.Leverage
                Strategy = strategy
                Status = "CREATED"
            }
            do! placeOrders cmd
            return ()
        } 
        |> Async.map (fun result -> 
                match result with
                | Ok _ -> ()
                | Result.Error ex -> Log.Error (ex, "Error closing signal: {ErrorMsg}", ex.Message)
            )

type Balance = {
    Symbol: Symbol
    Amount: decimal
}

type PositionKey = PositionKey of string
type BalanceKey  = BalanceKey  of string

let private tradeFeesPercent = 0.04m // assume the worst: current market order fees for Binance is 0.04% (https://www.binance.com/en/support/articles/360033544231)
let private positions = new ConcurrentDictionary<PositionKey, Position>()

let mutable started = false
type private TradeAgentCommand = 
    | FuturesAccountUpdate of BinanceFuturesStreamAccountUpdate // * AsyncReplyChannel<unit>
    | FuturesBookPrice of BinanceFuturesStreamBookPrice
    | RefreshPositions

let private printPositionSummary () =
    positions.Values
    |> Seq.iter (fun pos -> 
            match pos.CalculatedPnl, pos.CalculatedPnlPercent with
            // find the units of qty: so we know what the pnl is in.
            | Some pnl, Some pnlP ->
                let symbol = pos.Symbol.ToString()
                let found, symbolUnits = coinMSymbols.TryGetValue symbol
                let pnlUsd, unitName = 
                    if found then (pnl / decimal symbolUnits.Multiplier), "USD"
                    else pnl, "USDT"

                Log.Information("{Symbol} [{PositionSide}]: {Pnl:0.0000} {UnitName} [{PnlPercent:0.00} %]. Stop: {StopLossPercent:0.00} %", 
                    pos.Symbol.ToString(), 
                    pos.PositionSide,
                    pnlUsd, unitName, pnlP,
                    Option.defaultValue -9999M pos.StoplossPnlPercentValue) 
            | _ -> ()
        )
    Async.unit

let private getUsdtPositionsFromBinanceAPI (client: IBinanceClientFuturesUsdt) (symbol: string option) =
    async {
        let! positionResult =
            match symbol with
            | Some s -> client.GetPositionInformationAsync(symbol = s.Replace("_PERP", "")) //ugly hack
            | None   -> client.GetPositionInformationAsync()
            |> Async.AwaitTask
        
        let positions =
            if positionResult.Success
            then
                positionResult.Data
                |> Seq.filter (fun p -> p.PositionAmount <> 0m)
                |> Seq.map (fun p -> {
                        Position.EntryPrice = p.EntryPrice
                        Symbol = Symbol p.Symbol
                        Leverage = p.Leverage
                        IsolatedMargin = p.IsolatedMargin
                        LiquidationPrice = p.LiquidationPrice
                        MarginType = p.MarginType // cross or isolated
                        PositionSide = p.PositionSide // long or short
                        PositionAmount = p.PositionAmount
                        MarkPrice = p.MarkPrice
                        RealisedPnl = None // this is available only in the stream updates :/
                        CalculatedPnl = None // we'll update this when we have a price ticker update
                        CalculatedPnlPercent = None
                        StoplossPnlPercentValue = None
                        UnrealisedPnl = p.UnrealizedProfit // this does not consider fees etc
                        MaxQuantity = p.MaxNotionalValue // not sure what this is
                        IsStoppedOut = false
                        // EntryTime we don't actually know - unless we query orders and guess / calculate over time :|
                    })
            else
                Log.Error("Error getting USDT futures positions: {Error}. Symbol (None means all): {Symbol}", positionResult.Error, symbol)
                Seq.empty

        return positions
    }

let private getCoinPositionsFromBinanceAPI (client: IBinanceClientFuturesCoin) (symbol: string option) =
    async {
        let! positionResult =
            match symbol with
            | Some s -> client.GetPositionInformationAsync(pair = s.Replace("_PERP", ""))
            | None   -> client.GetPositionInformationAsync()
            |> Async.AwaitTask

        let positions =
            if positionResult.Success
            then
                positionResult.Data
                |> Seq.filter (fun p -> p.PositionAmount <> 0m)
                |> Seq.map (fun p -> {
                        Position.EntryPrice = p.EntryPrice
                        Symbol = Symbol p.Symbol
                        Leverage = p.Leverage
                        IsolatedMargin = p.IsolatedMargin
                        LiquidationPrice = p.LiquidationPrice
                        MarginType = p.MarginType // cross or isolated
                        PositionSide = p.PositionSide // long or short
                        PositionAmount = p.PositionAmount
                        MarkPrice = p.MarkPrice
                        RealisedPnl = None // only available in the stream update
                        CalculatedPnl = None // calculated when we have a price ticker update
                        CalculatedPnlPercent = None
                        StoplossPnlPercentValue = None
                        UnrealisedPnl = p.UnrealizedProfit
                        MaxQuantity = p.MaxQuantity // not sure what this is
                        IsStoppedOut = false
                        // EntryTime we don't actually know - unless we query orders and guess / calculate over time :|
                    })
            else
                Log.Error("Error getting COIN-M futures positions: {Error}. Symbol (None means all): {Symbol}", positionResult.Error, symbol)
                Seq.empty

        return positions
    }

let private makePositionKey symbol (positionSide: Enums.PositionSide) = PositionKey <| sprintf "%s-%s" symbol (positionSide.ToString())

let private savePositions (ps: Position seq) = 
    ps 
    |> Seq.iter (fun pos -> 
            let (Symbol s) = pos.Symbol
            let key = makePositionKey s pos.PositionSide
            let found, existingPosition = positions.TryGetValue key
            let updatedPosition = 
                if found then
                    {
                        pos with
                            CalculatedPnl = existingPosition.CalculatedPnl
                            CalculatedPnlPercent = existingPosition.CalculatedPnlPercent
                            IsStoppedOut = false // reset
                            StoplossPnlPercentValue = existingPosition.StoplossPnlPercentValue
                    }
                else
                    pos
            positions.[key] <- updatedPosition
        )

let private fetchPosition (client: BinanceClient) (p: BinanceFuturesStreamPosition) =
    let key = makePositionKey p.Symbol p.PositionSide
    let found, pos = positions.TryGetValue key
    async {
        let! pos' =
            match found, p.Symbol with
            | true, _ ->
                    async {
                        return Some({
                            pos with
                                EntryPrice = p.EntryPrice
                                MarginType = p.MarginType
                                PositionAmount = p.PositionAmount
                                PositionSide = p.PositionSide
                                RealisedPnl = Some p.RealizedPnL 
                                UnrealisedPnl = p.UnrealizedPnl
                        })
                    }
            | false, s when s.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ->
                getUsdtPositionsFromBinanceAPI client.FuturesUsdt (Some s)
                |> Async.map Seq.tryHead
            | false, s ->
                getCoinPositionsFromBinanceAPI client.FuturesCoin (Some s)
                |> Async.map Seq.tryHead
        match pos' with
        | Some p -> positions.[key] <- p
        | None -> 
            Log.Warning ("Could not get position from Binance API for symbol: {Symbol}. Removing position assuming it is closed.", p.Symbol)
            positions.Remove key |> ignore
     }

let private calculateStopLoss (position: Position) (gainOpt: decimal option) =
    let minStopLoss = -1.5M * decimal position.Leverage
    let previousStopLossValue = position.StoplossPnlPercentValue
    let previousGain = Option.defaultValue 0M position.CalculatedPnlPercent
    let gain = Option.defaultValue 0M gainOpt

    let stopLossOfAtleast prevStopLoss newStopLoss = 
        // by this time we've already got a stop loss.
        // we only ever change stoploss upward
        List.max [
            newStopLoss
            prevStopLoss / 1M
            minStopLoss
        ]
        |> Some

    match previousStopLossValue with
    | None ->
        Some minStopLoss // always start with the minstoploss    

    | Some v when gain > previousGain ->
        let newStopLoss = if gain > 0M then gain - (1.5M * decimal position.Leverage) else minStopLoss
        let sl = stopLossOfAtleast v newStopLoss
        sl

    | v -> v // no change in stop loss

let private calculatePnl (position: Position) (price: BinanceFuturesStreamBookPrice) =
    let qty = decimal <| Math.Abs(position.PositionAmount)
    let pnl = 
        match position.PositionSide with
        | Enums.PositionSide.Long ->
            qty * (price.BestAskPrice - position.EntryPrice) - (qty * 2m * tradeFeesPercent) |> Some
        | Enums.PositionSide.Short ->
            qty * (position.EntryPrice - price.BestBidPrice) - (qty * 2m * tradeFeesPercent)  |> Some
        | _ -> None
    let pnlPercent =
        pnl
        |> Option.map (fun pnl' ->
                if position.Leverage = 0 || position.PositionAmount = 0M || position.EntryPrice = 0M
                then None
                else 
                    let originalMargin = Math.Abs(position.PositionAmount) * position.EntryPrice / decimal position.Leverage
                    Some <| (100M * pnl' / originalMargin)
            )
        |> Option.flatten
    pnl, pnlPercent

let private isStopLossHit (position: Position) =
    match position.StoplossPnlPercentValue, position.CalculatedPnlPercent with
    | Some stoploss, Some gain when gain <= stoploss -> true 
    | _ -> false

let private printPositions (positions: Position seq) =
    positions
    |> Seq.filter (fun pos -> pos.PositionAmount <> 0m)
    |> Seq.iter (fun pos ->
            Log.Information("Position: {@Position}", pos) 
        )

let private cleanUpStoppedPositions () =
    let positionsToRemove =
        positions.Values
        |> Seq.filter (fun p -> p.IsStoppedOut)
        |> Seq.toList
    
    positionsToRemove
    |> Seq.map (fun p -> makePositionKey (p.Symbol.ToString()) p.PositionSide)
    |> Seq.iter (fun key -> positions.Remove key |> ignore)

let private refreshPositions (client: BinanceClient) =
    async {
        Log.Information ("Refreshing positions from Binance API")
        Log.Information "Getting COIN-M futures positions from Binance API"
        let! coinPositions = getCoinPositionsFromBinanceAPI client.FuturesCoin None
        savePositions coinPositions

        Log.Information "Getting USDT futures positions from Binance API"
        let! usdtPositions = getUsdtPositionsFromBinanceAPI client.FuturesUsdt None
        savePositions usdtPositions

        Log.Information "Now cleaning up old stopped out positions"
        cleanUpStoppedPositions ()

        Log.Information ("We have {PositionCount} positions now.", positions.Count)
        printPositions positions.Values
    }
 
let private updatePositionPnl (client: BinanceClient) (price: BinanceFuturesStreamBookPrice) =
    [ Enums.PositionSide.Long; Enums.PositionSide.Short ]
    |> List.map (makePositionKey price.Symbol)
    |> List.map (fun key ->  (positions.TryGetValue key), key)
    |> List.filter (fun ((found, pos), _) -> found && not pos.IsStoppedOut)
    |> List.iter (fun ((_, position), key) -> 
            let pnl, pnlPercent = calculatePnl position price
            let newStopLoss = calculateStopLoss position pnlPercent
            let pos' = 
                {
                    position with
                              CalculatedPnl = pnl
                              CalculatedPnlPercent = pnlPercent
                              StoplossPnlPercentValue = newStopLoss
                }
            
            let isStoppedOut = isStopLossHit pos'
            positions.[key] <- {
                pos' with
                     IsStoppedOut = isStoppedOut 
            }

            // raise signal / trade when stopped out
            if isStoppedOut then
                async {
                    do! closeSignal positions.[key] price
                    do! Async.Sleep (45 * 1000) // 45 seconds
                    do! refreshPositions client
                }
                |> Async.Start
        )

// serialising calls to be safe
let private mkTradeAgent (client: BinanceClient) =
    MailboxProcessor<TradeAgentCommand>.Start (fun inbox ->
        let rec messageLoop() = async {
            let! msg = inbox.Receive()

            try
                match msg with
                | FuturesAccountUpdate accountUpdate ->
                    
                    
                    // add or update positions from the incoming update
                    accountUpdate.UpdateData.Positions
                    |> Seq.map (fetchPosition client)
                    |> AsyncSeq.ofSeq
                    |> AsyncSeq.iterAsync id
                    |> Async.RunSynchronously

                    // do the reverse: remove any positions that are in-memory, but not in accountUpdate
                    positions.Values
                    |> Seq.map (fun p -> makePositionKey (p.Symbol.ToString()) p.PositionSide)
                    |> Seq.except (
                            accountUpdate.UpdateData.Positions 
                            |> Seq.map (fun p -> makePositionKey p.Symbol p.PositionSide)
                        )
                    |> Seq.iter (fun pos -> positions.Remove pos |> ignore)

                | FuturesBookPrice bookPrice ->
                    updatePositionPnl client bookPrice

                | RefreshPositions ->
                    refreshPositions client |> Async.RunSynchronously

            with e ->
                Log.Error (e, "Error handling msg: {@Message}", msg)

            return! messageLoop()
        }
        messageLoop()
    )

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

let private subscribeToUserStream (tradeAgent: MailboxProcessor<TradeAgentCommand>) (clientFutures: IBinanceClientFutures) (socketClientFutures: IBinanceSocketClientFutures) (listenKey: string) =
       socketClientFutures.SubscribeToUserDataUpdates (
           listenKey,
           onOrderUpdate = null,
           onLeverageUpdate = null,
           onAccountUpdate = (fun accUpdate -> tradeAgent.Post (FuturesAccountUpdate accUpdate)),
           onMarginUpdate = null,
           onListenKeyExpired = null
       )
    |> keepUserStreamSubscriptionAlive clientFutures listenKey

let private listenToFuturesPriceTickerForSymbol (tradeAgent: MailboxProcessor<TradeAgentCommand>) (socketClientFutures: IBinanceSocketClientFutures) (symbol: string) =
    let subscription = 
        socketClientFutures.SubscribeToBookTickerUpdates (
            symbol,
            onMessage = (fun msg -> tradeAgent.Post (FuturesBookPrice msg))
        )
    Log.Information("Subscribing to futures price ticker for {Symbol}. Success: {Result}. Error: {Error}", symbol, subscription.Success, subscription.Error)
    subscription.Success

let private listenFutures (tradeAgent: MailboxProcessor<TradeAgentCommand>) (clientFutures: IBinanceClientFutures) (socketClientFutures: IBinanceSocketClientFutures) (symbols: string seq) =
    let listenKeyResponse = clientFutures.UserStream.StartUserStream()
    if not listenKeyResponse.Success
    then failwith (sprintf "Error starting user stream: [%d] - %s" (listenKeyResponse.Error.Code.GetValueOrDefault()) listenKeyResponse.Error.Message)
    else
        subscribeToUserStream tradeAgent clientFutures socketClientFutures listenKeyResponse.Data

        symbols
        |> Seq.map (listenToFuturesPriceTickerForSymbol tradeAgent socketClientFutures)
        |> Seq.reduce ((&&))

let trackPositions () =
    use _x = LogContext.PushProperty ("Futures", true)

    let client = Binance.Futures.Trade.getBaseClient ()
    let socketClient = Binance.Futures.Trade.getSocketClient ()

    Log.Information "Starting socket client for Binance futures user data stream"

    let tradeAgent = mkTradeAgent client
    
    // need to add an error handler to ensure the process crashes properly
    // we might later need to make this smarter, to crash only on repeated exceptions of the same kind or 
    // something where the exception is unrecoverable
    tradeAgent.Error.Add(raise)

    tradeAgent.Post RefreshPositions

    repeatEvery (TimeSpan.FromSeconds(3.0)) printPositionSummary "PositionSummaryPrinter" |> Async.Start

    started <- 
        started || 
            listenFutures tradeAgent client.FuturesCoin socketClient.FuturesCoin coinMSymbols.Keys &&
            listenFutures tradeAgent client.FuturesUsdt socketClient.FuturesUsdt usdtSymbols.Keys
    started

