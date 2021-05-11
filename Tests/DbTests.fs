namespace IntegrationTests.Db

open FsUnit
open FsUnit.Xunit
open System
open Db
open Microsoft.VisualStudio.TestTools.UnitTesting
open DbTypes
open Types

module Db = Db.Common

[<TestClass>]
type GetBuyOrderForSell () = // (output: Xunit.Abstractions.ITestOutputHelper) =

    //do
    //    Log.Logger <- output.CreateSerilogLogger Events.LogEventLevel.Verbose

    let getBuyOrderForSignal s = 
        async {
            let! orders = Db.getOrdersForSignal s
            return (
                orders
                |> Seq.filter (fun o -> o.OrderSide = "BUY")
                |> Seq.tryHead
            )
        }

    //[<Fact>]
    [<TestMethod>]
    member _x.`` GetBuyOrderForSell Returns None for non-existent signal`` () =
        async {
            // we're assuming signal 0L doesn't exist
            let! buyOrder = getBuyOrderForSignal 0L
            buyOrder |> should equal None
        } |> Async.RunSynchronously

   //[<Fact>]
    [<TestMethod>]
    member _x.``GetBuyOrderForSell Returns correct data for existing signal`` () =
        async {
            let currentSignalId = DateTime.Now.Ticks
            let insertSignalSql = 
                "INSERT INTO public.positive_signal
                    (signal_id,
                     buy_price,
                     status,
                     symbol,
                     strategy,
                     actual_buy_price,
                     buy_date_time,
                     buy_signal_date_time,
                     market)
                VALUES (
                     @CurrentSignalId,
                     10,
                     'BUY_FILLED',
                     'ETHBTC',
                     'StrategyTest1',
                     10.01,
                     @BuyDateTime,
                     @BuySignalDateTime,
                     'BTC'
                );
                "
            let buySignalData = dict [
                ( "CurrentSignalId", currentSignalId :> obj )
                ( "BuyDateTime", DateTime.UtcNow :> obj )
                ( "BuySignalDateTime", DateTime.UtcNow :> obj)
            ]

            do! Db.save [
                ( insertSignalSql, buySignalData :> obj );
            ]

            //let getInsertedSignalId = "SELECT currval(pg_get_serial_sequence('positive_signal','signal_id'));"
            //let! signalIds = Db.get<int64> getInsertedSignalId
            //let currentSignalId = signalIds |> Seq.head

            let newBuyOrder = {
                ExchangeOrder.CreatedTime = DateTime.UtcNow
                Status = "TEST"
                Id = 0L
                StatusReason = "SOME REASON DONT CARE"
                Symbol = "ETHBTC"
                Price = 10M
                ExecutedPrice = 10M
                ExchangeOrderId = "EOID1"
                ExchangeOrderIdSecondary = currentSignalId |> string
                SignalId = currentSignalId
                UpdatedTime = DateTime.UtcNow
                OriginalQty = 0.1M
                ExecutedQty = 0.09M
                FeeAmount = 0.001M * 0.09M
                FeeCurrency = "ETH"
                ExchangeId = Binance.ExchangeId
                OrderSide = "BUY"
                LastTradeId = 0L
            }

            let! newOrderId = Db.saveOrder newBuyOrder TradeMode.SPOT

            let! buyOrder = getBuyOrderForSignal currentSignalId
            
            //clean up before the asserts: need to find a better way to roll-back
            do! Db.save [
                ( "DELETE FROM positive_signal WHERE signal_id = @SignalId", { SignalIdParam.SignalId = currentSignalId } :> obj )
                ( "DELETE FROM exchange_order WHERE signal_id = @SignalId", { SignalIdParam.SignalId = currentSignalId } :> obj )
            ]

            // test!
            (*
            There is a small difference in ticks for example:
            Expected: Equals 636759822081001091L
            Actual:   was 636759822081001090L

            Due to the difference in the time resolution in .NET vs PG
            *)
            newOrderId |> should be (greaterThan 0L)

            let bo = buyOrder.Value
            //bo.CreatedTime.Ticks |> should equal newBuyOrder.CreatedTime.Ticks
            
            bo.Status |> should equal newBuyOrder.Status
            bo.StatusReason |> should equal newBuyOrder.StatusReason
            bo.Symbol |> should equal newBuyOrder.Symbol
            bo.Price |> should equal newBuyOrder.Price

            bo.ExchangeOrderId |> should equal newBuyOrder.ExchangeOrderId
            bo.ExchangeOrderIdSecondary |> should equal newBuyOrder.ExchangeOrderIdSecondary
            bo.SignalId |> should equal newBuyOrder.SignalId
            bo.OriginalQty |> should equal newBuyOrder.OriginalQty
            bo.ExecutedQty |> should equal newBuyOrder.ExecutedQty

            //buyOrder.Value |> should equal ({ 
            //    newBuyOrder with 
            //        Id = buyOrder.Value.Id
            //        CreatedTime = buyOrder.Value.CreatedTime
            //        UpdatedTime = buyOrder.Value.UpdatedTime
            //})
            
        } |> Async.RunSynchronously

    [<TestMethod>]
    member _x.``Update order works for existing exchange order`` () =
        async {
            let currentSignalId = DateTime.Now.Ticks
            let insertSignalSql = 
                "INSERT INTO public.positive_signal
                    (signal_id,
                     buy_price,
                     status,
                     symbol,
                     strategy,
                     actual_buy_price,
                     buy_date_time,
                     buy_signal_date_time,
                     market)
                VALUES (
                     @CurrentSignalId,
                     10,
                     'BUY_FILLED',
                     'ETHBTC',
                     'StrategyTest1',
                     10.01,
                     @BuyDateTime,
                     @BuySignalDateTime,
                     'BTC'
                );
                "
            let buySignalData = dict [
                ( "CurrentSignalId", currentSignalId :> obj )
                ( "BuyDateTime", DateTime.UtcNow :> obj )
                ( "BuySignalDateTime", DateTime.UtcNow :> obj)
            ]

            do! Db.save [
                ( insertSignalSql, buySignalData :> obj );
            ]

            //let getInsertedSignalId = "SELECT currval(pg_get_serial_sequence('positive_signal','signal_id'));"
            //let! signalIds = Db.get<int64> getInsertedSignalId
            //let currentSignalId = signalIds |> Seq.head

            let newBuyOrder = {
                ExchangeOrder.CreatedTime = DateTime.UtcNow
                Status = "TEST"
                Id = 0L
                StatusReason = "SOME REASON DONT CARE"
                Symbol = "ETHBTC"
                Price = 10M
                ExecutedPrice = 10M
                ExchangeOrderId = "EOID2"
                ExchangeOrderIdSecondary = currentSignalId |> string
                SignalId = currentSignalId
                UpdatedTime = DateTime.UtcNow
                OriginalQty = 0.1M
                ExecutedQty = 0.09M
                FeeAmount = 0.001M * 0.09M
                FeeCurrency = "ETH"
                ExchangeId = Binance.ExchangeId
                OrderSide = "BUY"
                LastTradeId = 0L
            }

            let! newOrderId = Db.saveOrder newBuyOrder TradeMode.SPOT

            let updatedBuyOrder = {
                newBuyOrder with
                    Status = "TEST-UP"
            }
            let! updatedOrderId = Db.saveOrder updatedBuyOrder TradeMode.SPOT

            let! buyOrder = getBuyOrderForSignal currentSignalId

            //clean up before the asserts: need to find a better way to roll-back
            do! Db.save [
                ( "DELETE FROM positive_signal WHERE signal_id = @SignalId", { SignalIdParam.SignalId = currentSignalId } :> obj )
                ( "DELETE FROM exchange_order WHERE signal_id = @SignalId", { SignalIdParam.SignalId = currentSignalId } :> obj )
            ]

            // test!
            (*
            There is a small difference in ticks for example:
            Expected: Equals 636759822081001091L
            Actual:   was 636759822081001090L

            Due to the difference in the time resolution in .NET vs PGSQL
            *)

            let bo = buyOrder.Value
            //bo.CreatedTime.Ticks |> should equal newBuyOrder.CreatedTime.Ticks

            newOrderId |> should be (greaterThan 0L)
            updatedOrderId |> should equal newOrderId
            
            bo.Status |> should equal updatedBuyOrder.Status
            
        } |> Async.RunSynchronously
