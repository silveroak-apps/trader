module Bybit.Futures.Trade

open Types
open Bybit.Futures

let getExchange() = {
    new IFuturesExchange with
    member __.CancelOrder(o: OrderQueryInfo): Async<Result<bool,string>> = 
        failwith "Not Implemented"
    member __.GetFuturesPositions(o: Symbol option): Async<Result<seq<ExchangePosition>,string>> = 
        failwith "Not Implemented"
    member __.GetOrderBookCurrentPrice(o: string): Async<Result<OrderBookTickerInfo,string>> = 
        failwith "Not Implemented"
    member __.Id   = ExchangeId Common.ExchangeId
    member __.Name = "BybitFutures"
    member __.PlaceOrder(o: OrderInputInfo): Async<Result<OrderInfo,OrderError>> = 
        failwith "Not Implemented"
    member __.QueryOrder(o: OrderQueryInfo): Async<OrderStatus> = 
        failwith "Not Implemented"
    member __.TrackPositions(agent: MailboxProcessor<PositionCommand>, symbols: Symbol seq): Async<unit> = 
        PositionListener.trackPositions agent symbols
}