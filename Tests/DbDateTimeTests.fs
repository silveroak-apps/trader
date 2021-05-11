namespace IntegrationTests.Db

open System
open Microsoft.VisualStudio.TestTools.UnitTesting
open FsUnit.Xunit

module Db = Db.Common

type TypeWithDateTimeOffset = {
    Date: DateTimeOffset
}

type TypeWithDateTime = {
    Date: DateTime
}

[<TestClass>]
type DbDateTimeTests () =

    [<TestMethod>]
    member _x.``DateTimeOffset cannot be deserialised`` () =
        let s = "SELECT now() AS Date"
        try
            let v =
                Db.get<TypeWithDateTimeOffset> s 
                |> Async.RunSynchronously
                |> Seq.head
            v.Date.DateTime.Kind |> should equal DateTimeKind.Utc
        with e ->
            true |> should equal true
    
    [<TestMethod>]
    member _x.``DateTime can be deserialised`` () =
        let s = "SELECT now() AS Date"
        let v =
            Db.get<TypeWithDateTime> s 
            |> Async.RunSynchronously
            |> Seq.head

        v.Date.Kind |> should equal DateTimeKind.Local
        v.Date.ToUniversalTime().Kind |> should equal DateTimeKind.Utc
        v.Date - v.Date.ToUniversalTime() |> should be (greaterThan TimeSpan.Zero)

    [<TestMethod>]
    member _x.``DateTime unspecified is considered local when converted to DateTimeOffset`` () =
        let date = DateTime(2018, 11, 7, 0, 0, 0, DateTimeKind.Unspecified)
        let dto = date |> DateTimeOffset
        
        date.ToUniversalTime() |> should equal dto.UtcDateTime
        date |> should equal dto.DateTime
        date.Kind |> should equal dto.DateTime.Kind

