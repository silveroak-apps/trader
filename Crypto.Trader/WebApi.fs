module WebApi

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe
open FSharp.Control.Tasks.V2
open Serilog

// ugly mutable
let mutable private webApp: HttpHandler = fun (h) -> h

let private makeDbSignalHandler placeRealOrders : HttpHandler = 
    handleContext (
        fun ctx ->
            task {
                // go through futures and spot signals from db - once:

                // TODO refactor this properly:
                Trade.Futures.processValidSignals 
                    Db.getFuturesSignalCommands
                    Db.setSignalCommandsExpired
                    Db.getExchangeOrder
                    Db.saveOrder
                    placeRealOrders |> Async.StartAsTask |> ignore

                Trade.Spot.processValidSignals 
                    Db.getSignalsToBuyOrSell
                    Db.setSignalsExpired
                    Db.getOrdersForSignal
                    Db.getExchangeOrder
                    Db.saveOrder
                    placeRealOrders |> Async.StartAsTask |> ignore

                ctx.SetStatusCode 200
                return! ctx.WriteJsonAsync "OK"
            }
    )

// TODO: we might need to optimise to make this just listen to trades via API and save to DB as a side-effect, rather than just using the db as primary
// and then we can move to a SQLite / in-memory db if needed and use a ORM to simplify things
let private makeWebApp dbSignalHandler =
    choose [
        GET >=>
            choose [
                route "/" >=> json (dict [("status", "Healthy")])
            ]
        POST >=>
            choose [
                route "/tradeSignals" >=> dbSignalHandler
            ]
        setStatusCode 404 >=> text "Not Found" ]

let private errorHandler (ex : Exception) (logger : Microsoft.Extensions.Logging.ILogger) =
    logger.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

let private configureApp (app : IApplicationBuilder) =
    let env = app.ApplicationServices.GetService<IWebHostEnvironment>()
    (match env.EnvironmentName.Contains("dev") with
    | true  -> app.UseDeveloperExceptionPage()
    | false -> app.UseGiraffeErrorHandler errorHandler)
                  .UseGiraffe(webApp)

let private configureServices (services : IServiceCollection) =
    services.AddGiraffe() |> ignore

let run realOrders =
    // let tradeHandler = makeTradeHandler realOrders
    let dbSignalHandler = makeDbSignalHandler realOrders
    webApp <- makeWebApp dbSignalHandler
    WebHostBuilder()
        .UseKestrel()
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureServices(configureServices)
        .UseSerilog()
        .Build()
        .Run()

