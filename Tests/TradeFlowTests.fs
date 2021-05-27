namespace Tests

open System
open Xunit
open FsUnit.Xunit

open Trade
open DbTypes
open System.Collections.Generic
open FsUnit.CustomMatchers

module TradeFlowTests =


    [<Fact>]
    let ``Trade placeOrder retries 10 times on error`` () = 
        let callCounts = 
            dict [
                ("PlaceOrder", 0)
                ("QueryOrder", 0)
                ("CancelOrder", 0)
                ("GetOrderBookCurrentPrice", 0)
            ] |> (fun d -> Dictionary<string, int>(d))

        let called s = callCounts.[s] <- 1 + if callCounts.ContainsKey s then callCounts.[s] else 0
        
        let mockExchange = 
            {
                new Types.IExchange with
                member __.PlaceOrder _ = 
                    "PlaceOrder" |> called
                    raise (exn "PlaceOrder mock not setup")
                member __.QueryOrder _ = raise (exn "QueryOrder mock not setup")
                member __.CancelOrder _ = raise (exn "CancelOrder mock not setup")
                member __.GetOrderBookCurrentPrice _ = raise (exn "GetOrderBookCurrentPrice mock not setup")
            }
        let signalCmd = {
            FuturesSignalCommandView.SignalId = 1L
            Id = 1L
            ExchangeId = 0L
            Price = 1M
            Quantity = 1M
            Symbol = "does not matter"
            Action = "OPEN"
            RequestDateTime = DateTime.UtcNow
            ActionDateTime = DateTime.MinValue
            PositionType = "LONG"
            Leverage = 1
            Strategy = "does not matter"
            Status = "does not matter"
        }
        let result = Futures.placeOrderWithRetryOnError mockExchange signalCmd 0M |> Async.RunSynchronously
        result |> should be (ofCase <@ Error @>)