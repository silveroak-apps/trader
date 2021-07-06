module Db.Common

open Dapper
open Npgsql
open Serilog
open FSharp.Control
open FSharpx.Control
open DbTypes
open System
open System.Data

// Dapper: enabling mappingsnake_case names of Postgres columns to .NET PropertyNames doesn't seem to work
// seems to cause problems
// DefaultTypeMap.MatchNamesWithUnderscores <- true

let mapNullable (mapper: 'T -> 'TOut) (n: Nullable<'T>) =
    if n.HasValue
    then mapper n.Value |> Nullable
    else Nullable<'TOut>()

let unspecToUtcKind (dt: DateTime) = 
    if dt.Kind = DateTimeKind.Unspecified 
    then DateTime.SpecifyKind(dt, DateTimeKind.Utc) 
    else dt

let mkConnectionFor s = new NpgsqlConnection (s)
let mkConnection () = mkConnectionFor pgsqlConnectionString

let getWithParam<'T> (sql: string) (param: obj) =
    async {
        use cnn = mkConnection ()
        let! data = cnn.QueryAsync<'T> (sql, param) |> Async.AwaitTask
        return data
    }

let getWithConnection<'T> (cnnString: string) (sql: string) =
    async {
        use cnn = mkConnectionFor cnnString
        let! data = cnn.QueryAsync<'T> sql |> Async.AwaitTask
        return data
    }

let getWithConnectionAndParam<'T> (cnn: IDbConnection) (sql: string) (param: obj) =
    async {
        return! cnn.QueryAsync<'T> (sql, param) |> Async.AwaitTask
    }

let getWithConnectionStringAndParam<'T> (cnnString: string) (sql: string) (param: obj) =
    async {
        try
            use cnn = mkConnectionFor cnnString
            return! getWithConnectionAndParam<'T> cnn sql param
        with e ->
            Log.Warning (e, "Error running sql (Param: {Param}) {Sql} on db {ConnectionString}: {Error}", param, sql, cnnString, e.Message)
            
            raise e
            return Seq.empty
    }

let get<'T> (sql: string) =
    async {
        use cnn = mkConnection ()
        let! data = cnn.QueryAsync<'T> sql |> Async.AwaitTask
        return data
    }

let saveUsing (cnn: IDbConnection) (sqls: (string * obj) seq) =
    async {
        let operations = 
            AsyncSeq.ofSeq sqls
            |> AsyncSeq.iterAsync (
                fun (sql, data) -> 
                    cnn.ExecuteAsync (sql, data) 
                    |> Async.AwaitTask 
                    |> Async.Ignore
                )
        return! operations
    }

let save (sqls: (string * obj) seq) =
    async {
        use cnn = mkConnection ()
        do! cnn.OpenAsync() |> Async.AwaitTask
        use tx = cnn.BeginTransaction()
        try
            do! saveUsing cnn sqls
            tx.Commit()
        with e ->
            tx.Rollback()
            Log.Error (e, "Error saving data. {Data}", sqls)
        return ()
    }

type Heartbeat = {
    ResourceName: string
}

let saveHeartbeat name =
    let heartbeatSql = "
        INSERT INTO watchdog (id, resource, last_update)
        VALUES ((SELECT coalesce(max(id), 0::int8) + 1 FROM watchdog), @ResourceName, (now() - interval '10 seconds')::timestamp)

        ON CONFLICT (resource) DO

        UPDATE SET last_update = (now()- interval '10 seconds')::timestamp;
    "
    save [
        (heartbeatSql, { Heartbeat.ResourceName = name } :> obj)
    ]
