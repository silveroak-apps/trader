module Simulator
open System
    open System.Collections.Generic
    open Types

let ExchangeId = 0L

module Exchange =

    let private rnd = Random()

    let private placeOrder (o: OrderInputInfo) : Async<Result<OrderInfo, OrderError>> =
        // fake a success and save order in db
        (*
        80 % : buy_filled
        10 % : partially_filled
        10 % : cancelled / error
        *)

        let (status, executedQtyPercent) =
            OrderFilled(o.Quantity, o.Price), 1.0
            // disabling again because this isn't implemented properly.
            // we don't automatically handle partial fills at the moment
            // the simulator exchange leaves it as-is and we're stuck
            // match rnd.NextDouble() with
            // | v when v >= 0.0 && v <= 0.8 -> OrderFilled(o.Quantity, o.Price), 1.0
            // | v when v >  0.8  && v <= 0.9 -> OrderPartiallyFilled(o.Quantity, (decimal v) * 1M<price>), v
            // | v when v >  0.9  && v <= 1.0 -> OrderCancelled(o.Quantity, (decimal v) * 1M<price>), 0.0
            // | _ -> OrderNew, 0.0 // this will never happen: it's only to keep the compiler happy, rnd.NextDouble() cases are all already covered above

        let origQty = o.Quantity /1M<qty>
        let executedQty = origQty * decimal executedQtyPercent
        let createTime = DateTimeOffset.UtcNow
        let orderId = OrderId <| string DateTime.UtcNow.Ticks

        async {
            return Ok <| {
                OrderInfo.ClientOrderId = ClientOrderId (string o.SignalId)
                Quantity = o.Quantity
                Price = o.Price
                Symbol = o.Symbol
                Status = status
                ExecutedQuantity = executedQty * 1M<qty>
                OrderId = orderId
                Time = createTime
            }
        }

    let get(innerExchange: IExchange) = {
            new IExchange with
            member __.PlaceOrder o = async {
                    let! res = placeOrder o
                    return res
                }
            member __.QueryOrder _ = async {
                    return OrderPartiallyFilled(0M<qty>, 0M<price>) // will this work?
                }
            member __.CancelOrder _ = async {
                    return Ok true
                }
            member __.GetOrderBookCurrentPrice s = innerExchange.GetOrderBookCurrentPrice s
        }
