module Binance.ApiTypes

open Newtonsoft.Json
open Microsoft.FSharpLu.Json

let serializerSettings = 
            JsonSerializerSettings (
                Converters = [|
                        // our classes are F# types with unions. 
                        // This converter does a better job of serializing those types
                        new CompactUnionJsonConverter()
                    |]
            )

type BinanceApiKey = {
    Key : string
    Secret : string
}

type UnixTime = int64

(*
    { "serverTime": 1515499278236 }
*)
type BinanceServerTime = {
    ServerTime: UnixTime
}

(*
    {
        "symbol": "LTCBTC",
        "orderId": 17790831,
        "clientOrderId": "vEic6s35gleNPcRiFMOm0r",
        "transactTime": 1515502353709,
        "price": "0.00671800",
        "origQty": "1.00000000",
        "executedQty": "0.00000000",
        "status": "NEW",
        "timeInForce": "GTC",
        "type": "LIMIT",
        "side": "BUY"
    }
*)
type BinanceOrderSuccessResponse = {
    Symbol: string
    OrderId: int
    ClientOrderId: string
    TransactTime: UnixTime
    Price: decimal
    OrigQty: decimal
    ExecutedQty: decimal
    Status: string
    TimeInForce: string
    Type: string
    Side: string
}
with
    static member Parse (json: string) = 
        try
            JsonConvert.DeserializeObject<BinanceOrderSuccessResponse> (json, serializerSettings) |> Ok
        with e -> Error e

(*
    {
      "symbol": "LTCBTC",
      "orderId": 1,
      "clientOrderId": "myOrder1",
      "price": "0.1",
      "origQty": "1.0",
      "executedQty": "0.0",
      "status": "NEW",
      "timeInForce": "GTC",
      "type": "LIMIT",
      "side": "BUY",
      "stopPrice": "0.0",
      "icebergQty": "0.0",
      "time": 1499827319559,
      "isWorking": true
    }
*)
type BinanceOrderQueryResponse = {
    Symbol: string
    OrderId: int64
    ClientOrderId: string
    Price: decimal
    OrigQty: decimal
    ExecutedQty: decimal
    Status: string
    TimeInForce: string
    Type: string
    Side: string
    StopPrice: decimal
    IcebergQty: decimal
    Time: UnixTime
    IsWorking: bool
}
with
    static member Parse (json: string) = 
        JsonConvert.DeserializeObject<BinanceOrderQueryResponse> (json, serializerSettings) //|> Ok


(*
    [
      {
        "id": 28457,
        "orderId": 100234,
        "price": "4.00000100",
        "qty": "12.00000000",
        "commission": "10.10000000",
        "commissionAsset": "BNB",
        "time": 1499865549590,
        "isBuyer": true,
        "isMaker": false,
        "isBestMatch": true
      }
    ]
*)
type BinanceMyTradesResponse = {
    Id: int64
    OrderId: int64
    Price: decimal
    Qty: decimal
    Commission: decimal
    CommissionAsset: string
    time: UnixTime
    IsBuyer: bool
    IsMaker: bool
    IsBestMatch: bool
}
with
    static member ParseArray (json: string) = 
        JsonConvert.DeserializeObject<BinanceMyTradesResponse array> (json, serializerSettings) //|> Ok

(*
[
  {
    "symbol": "LTCBTC",
    "orderId": 1,
    "clientOrderId": "myOrder1",
    "price": "0.1",
    "origQty": "1.0",
    "executedQty": "0.0",
    "cummulativeQuoteQty": "0.0",
    "status": "NEW",
    "timeInForce": "GTC",
    "type": "LIMIT",
    "side": "BUY",
    "stopPrice": "0.0",
    "icebergQty": "0.0",
    "time": 1499827319559,
    "updateTime": 1499827319559,
    "isWorking": true
  }
]
*)
type BinanceMyOrdersResponse = {
    Symbol: string
    OrderId: int64
    ClientOrderId: string
    Price: decimal
    OrigQty: decimal
    ExecutedQty: decimal
    CummulativeQuoteQty: decimal
    Status: string
    TimeInForce: string
    Type: string 
    Side: string
    StopPrice: decimal
    IcebergQty: decimal
    Time: UnixTime
    UpdateTime: UnixTime
    IsWorking: bool
}
with
    static member ParseArray (json: string) = 
        JsonConvert.DeserializeObject<BinanceMyOrdersResponse array> (json, serializerSettings) //|> Ok

(*
    {
      "timezone": "UTC",
      "serverTime": 1515785965794,
      "rateLimits": [
        {
          "rateLimitType": "REQUESTS",
          "interval": "MINUTE",
          "limit": 1200
        },
        {
          "rateLimitType": "ORDERS",
          "interval": "SECOND",
          "limit": 10
        },
        {
          "rateLimitType": "ORDERS",
          "interval": "DAY",
          "limit": 100000
        }
      ],
      "exchangeFilters": [],
      "symbols": [
        {
          "symbol": "ETHBTC",
          "status": "TRADING",
          "baseAsset": "ETH",
          "baseAssetPrecision": 8,
          "quoteAsset": "BTC",
          "quotePrecision": 8,
          "orderTypes": [
            "LIMIT",
            "LIMIT_MAKER",
            "MARKET",
            "STOP_LOSS_LIMIT",
            "TAKE_PROFIT_LIMIT"
          ],
          "icebergAllowed": true,
          "filters": [
            {
              "filterType": "PRICE_FILTER",
              "minPrice": "0.00000100",
              "maxPrice": "100000.00000000",
              "tickSize": "0.00000100"
            },
            {
              "filterType": "LOT_SIZE",
              "minQty": "0.00100000",
              "maxQty": "100000.00000000",
              "stepSize": "0.00100000"
            },
            {
              "filterType": "MIN_NOTIONAL",
              "minNotional": "0.00100000"
            }
          ]
        }
      ]
    }
*)
type BinanceFilterInfo = {
    FilterType: string
    MinPrice: decimal option
    MaxPrice: decimal option
    TickSize: decimal option
    MinQty: decimal option
    MaxQty: decimal option
    StepSize: decimal option
    MinNotional: decimal option
}
type BinanceSymbolInfo = {
    Symbol: string
    BaseAsset: string
    QuoteAsset: string
    Filters: BinanceFilterInfo array
}
type BinanceExchangeInfoResponse = {
    Symbols: BinanceSymbolInfo array
}
with
    static member Parse (json: string) = 
        JsonConvert.DeserializeObject<BinanceExchangeInfoResponse> (json, serializerSettings) //|> Ok

(*
    {
        "symbol": "ETHBTC",
        "price": "0.09062100"
    }
*)
type BinanceSymbolTickerResponse = {
    Symbol: string
    Price: decimal
}
with
    static member Parse (json: string) = 
        JsonConvert.DeserializeObject<BinanceSymbolTickerResponse> (json, serializerSettings) //|> Ok

(*
/api/v3/ticker/bookTicker
{
    "symbol": "ETHBTC",
    "bidPrice": "0.07946700",
    "bidQty": "9.00000000",
    "askPrice": "100000.00000000",
    "askQty": "1000.00000000"
  }

*)
type BinanceOrderBookTickerResponse = {
    Symbol: string
    BidPrice: decimal
    BidQty: decimal
    AskPrice: decimal
    AskQty: decimal
}
with
    static member Parse (json: string) = 
        JsonConvert.DeserializeObject<BinanceOrderBookTickerResponse> (json, serializerSettings) //|> Ok

type BinanceSymbolBalance = {
    Asset: string
    Free: decimal
    Locked: decimal
}
with
    static member Parse (json: string) = 
        JsonConvert.DeserializeObject<BinanceSymbolBalance> (json, serializerSettings) //|> Ok

type BinanceAccountResponse = {
    MakerCommission: int
    TakerCommission: int
    BuyerCOmmission: int
    SellerCommission: int
    CanTrade: bool
    CanWithdraw: bool
    CanDeposit: bool
    UpdateTime: double
    Balances: BinanceSymbolBalance list
}
with
    static member Parse (json: string) = 
        JsonConvert.DeserializeObject<BinanceAccountResponse> (json, serializerSettings) //|> Ok