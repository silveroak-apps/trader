module Bybit.Futures.PositionListener
open Types

let private trackPositions (agent: MailboxProcessor<PositionCommand>) (symbols: string seq) =
    async {
        return ()
    }

let getExchange1() = {
    new IFuturesExchange with
    member __.CancelOrder(o: OrderQueryInfo): Async<Result<bool,string>> = 
        failwith "Not Implemented"
    member __.GetFuturesPositions(o: string option): Async<Result<seq<ExchangePosition>,string>> = 
        failwith "Not Implemented"
    member __.GetOrderBookCurrentPrice(o: string): Async<Result<OrderBookTickerInfo,string>> = 
        failwith "Not Implemented"
    member __.Id   = ExchangeId Common.ExchangeId
    member __.Name = "BybitFutures"
    member __.PlaceOrder(o: OrderInputInfo): Async<Result<OrderInfo,OrderError>> = 
        failwith "Not Implemented"
    member __.QueryOrder(o: OrderQueryInfo): Async<OrderStatus> = 
        failwith "Not Implemented"
    member __.TrackPositions(agent: MailboxProcessor<PositionCommand>, symbols: seq<string>): Async<unit> = 
        trackPositions agent symbols
}