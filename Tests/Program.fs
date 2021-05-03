module Program

open Serilog
open System
open DbTests

[<EntryPoint>]
let main _ =
    Log.Logger <- LoggerConfiguration()
                        .WriteTo.Console()
                        .CreateLogger()

    // GetBuyOrderForSell().``GetBuyOrderForSell Returns correct data for existing signal``()
    FuturesTrade.IntegrationTests.``Process valid signals expires old signal commands``()
    Console.ReadLine() |> ignore
    0