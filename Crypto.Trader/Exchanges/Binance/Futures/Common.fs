module Binance.Futures.Common

open System
open Serilog
open Binance.Net
open Binance.ApiTypes

let private cfg = appConfig.GetSection "Binance"

let getApiKeyCfg () = 
    {
        BinanceApiKey.Key = cfg.Item "FuturesKey"
        Secret = cfg.Item "FuturesSecret"
    }

let getBaseClient () =
    let apiKey = getApiKeyCfg ()
    let options = 
        // this is options for everything - though it has 'Spot' in its name
        let opts = 
            let o' = Objects.Spot.BinanceClientOptions(TradeRulesBehaviour = Enums.TradeRulesBehaviour.AutoComply)
            if apiKey.Key.Length > 0 && apiKey.Secret.Length > 0
            then o'.ApiCredentials <- new CryptoExchange.Net.Authentication.ApiCredentials(apiKey.Key, apiKey.Secret)
            o'

        // a little ugly - but will do for now
        let futuresCoinMBaseUrl = cfg.Item "FuturesCoinMBaseUrl" // Can be used to configure 'testnet'. Leave empty for default / prod
        let futuresUsdtBaseUrl = cfg.Item "FuturesUsdtBaseUrl" // Can be used to configure 'testnet'. Leave empty for default / prod
        if not <| String.IsNullOrWhiteSpace futuresCoinMBaseUrl
        then opts.BaseAddressCoinFutures <- futuresCoinMBaseUrl
            
        if not <| String.IsNullOrWhiteSpace futuresUsdtBaseUrl
        then opts.BaseAddressUsdtFutures <- futuresUsdtBaseUrl

        opts

    let binanceOptions = new BinanceClient(options)
    // Log.Verbose("Using Binance URLs: coin-m = {FuturesCoinMBaseUrl}, usdt = {FuturesUsdtBaseUrl}", 
        // options.BaseAddressCoinFutures, options.BaseAddressUsdtFutures)

    binanceOptions

let getSocketClient () =
    let apiKey = getApiKeyCfg ()
    let options = 
        Objects.Spot.BinanceSocketClientOptions ( // though this says 'Spot', it is really futures
                ApiCredentials = new CryptoExchange.Net.Authentication.ApiCredentials(apiKey.Key, apiKey.Secret),
                AutoReconnect = true
            )
    let futuresWssUrl = cfg.Item "FuturesWSSUrl"
    if not <| String.IsNullOrWhiteSpace futuresWssUrl
    then
        options.BaseAddressCoinFutures <- futuresWssUrl
        options.BaseAddressUsdtFutures <- futuresWssUrl

    let socketClient = new BinanceSocketClient (options)
    socketClient

let ExchangeId = 4L

// TODO move this to config / db?
// UnitName is what the 'quantity' refers to in the API calls.
// From there we derive a USD value - if it is USDT, we leave it as is.
// If it is 'CONT' or contracts, we can convert to USD

let usdtSymbols  = 
    dict [ 
        (Symbol "BNBUSDT",   { Types.ContractDetails.Multiplier = 1 })
        //(Symbol "BTCUSDT",   { Multiplier = 1 })
        //(Symbol "ETHUSDT",   { Multiplier = 1 })
        //(Symbol "ADAUSDT",   { Multiplier = 1 })
        //(Symbol "DOTUSDT",   { Multiplier = 1 })
        //(Symbol "DOGEUSDT",  { Multiplier = 1 })
        //(Symbol "MATICUSDT", { Multiplier = 1 })
        //(Symbol "LUNAUSDT",  { Multiplier = 1 })
    ]

let coinMSymbols  = 
    dict [ 
        (Symbol "BNBUSD",   { Types.ContractDetails.Multiplier = 10 })
        //(Symbol "BTCUSD",   { Multiplier = 100 })
        //(Symbol "ETHUSD",   { Multiplier = 10 })
        //(Symbol "ADAUSD",   { Multiplier = 10 })
        (Symbol "DOTUSD",   { Multiplier = 10 })
    ]