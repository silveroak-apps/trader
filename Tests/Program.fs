module Program

open Serilog
open System

[<EntryPoint>]
let main _ =
    Log.Logger <- LoggerConfiguration()
                        .WriteTo.Console()
                        .CreateLogger()

    // GetBuyOrderForSell().``GetBuyOrderForSell Returns correct data for existing signal``()
    IntegrationTests.FuturesTrade.``Process valid signals expires old signal commands``()
    Console.ReadLine() |> ignore
    0