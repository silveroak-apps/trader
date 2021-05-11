namespace IntegrationTests

open System
open Xunit
open FsUnit.Xunit

module FuturesTrade =

    let setupSignal () =
        let insertSql = "
            INSERT INTO public.futures_signal (
             signal_id,
             status,
             symbol,
             strategy,
             created_date_time,
             exchange_id
            )
            VALUES (
                @SignalId, 
                'OPEN',
                'BTCUSDTTEST',
                'Futures Trader Integration Test',
                @CreatedDateTime,
                0
            );
        "
        let signalId = 123L
        let signalData = dict [
                ( "SignalId", signalId :> obj )
                ( "CreatedDateTime", DateTime.UtcNow :> obj )
            ]
        async { 
            do! Db.Common.save [
                insertSql, signalData :> obj
            ]
            return signalId
        }
        
    let setupSignalCommands (signalId: int64) =
        let insertSql = "
            INSERT INTO futures_signal_command (
              id,
              signal_id,
              price,
              quantity,
              leverage,
              signal_action,
              position_type,
              status,
              request_date_time
            )
            VALUES (
                @Id,
                @SignalId,
                230,
                1,
                1,
                'OPEN',
                @RequestDateTime
            );
        "
        let commandId = DateTime.UtcNow.Ticks
        let signalCommandData = dict [
                ( "Id", commandId :> obj )
                ( "SignalId", signalId :> obj )
                ( "RequestDateTime", DateTime.UtcNow.AddSeconds(-11.0) :> obj ) // old command
            ]
        async { 
            do! Db.Common.save [
                insertSql, signalCommandData :> obj
            ]

            return commandId
        }

    let getSignalCommand (commantId: int64) =
        async {
            let! cmds = Db.getFuturesSignalCommands() //a bit lazy, but this will do for now
            let cmd = cmds |> Seq.find (fun c -> c.Id = commantId)
            return cmd
        }

    [<Fact>]
    let ``Process valid signals expires old signal commands`` () = 
        // setup a signal, and signal command that is older than 10 seconds
        // process valid signals and test that it has been marked as invalid in the db
        async {
            let! signalId = setupSignal ()
            let! commandId = setupSignalCommands signalId
            do! Trade.Futures.processValidSignals
                                Db.getFuturesSignalCommands
                                Db.setSignalCommandsComplete
                                Db.getExchangeOrder
                                Db.saveOrder
                                Db.getPositionSize
                                false

            // check if the command is marked 'expired'
            let! cmd = getSignalCommand commandId
            cmd.Status |> should equal "COMMAND_EXPIRED"
        } |> Async.RunSynchronously