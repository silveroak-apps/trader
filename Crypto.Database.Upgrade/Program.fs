open System
open Microsoft.Extensions.Configuration
open System.IO
open System.Reflection
open DbUp.Engine
open DbUp
open DbUp.Helpers

let showErrors (result: DatabaseUpgradeResult) =
    Console.ForegroundColor <- ConsoleColor.Red
    Console.WriteLine (result.Error)
    Console.ResetColor()
#if DEBUG
    Console.ReadLine() |> ignore
#endif
    result

let isSqlScript (name: string) = name.EndsWith ".sql"

let shouldAlwaysRun (name: string) = name.Contains "Scripts.AlwaysRun"

let upgrade (connectionString: string) =
    let upgrader =
        DeployChanges.To
                     .PostgresqlDatabase(connectionString)
                     .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), fun s -> isSqlScript s && (not <| shouldAlwaysRun s))
                     .LogToConsole()
                     .WithExecutionTimeout(Nullable <| TimeSpan.FromSeconds 360.0)
                     .Build()
    upgrader.PerformUpgrade()
    |> showErrors

let alwaysRunScripts (connectionString: string) =
    let upgrader =
        DeployChanges.To
                     .PostgresqlDatabase(connectionString)
                     .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly(), fun s -> isSqlScript s && shouldAlwaysRun s)
                     .JournalTo(new NullJournal())
                     .WithTransaction()
                     .LogToConsole()
                     .Build()

    upgrader.PerformUpgrade()
    |> showErrors

[<EntryPoint>]
let main argv =
    try
        let builder = 
            ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json")
                .AddEnvironmentVariables()

        let configuration = builder.Build()

        let connectionString = configuration.["DbAdminConnection"]

        Console.WriteLine ("Starting database upgrade using connection string: " + connectionString)
        let result = upgrade connectionString
        if (not result.Successful)
        then -1
        else
            let result = alwaysRunScripts connectionString
            if (not result.Successful)
            then -1
            else
            Console.ForegroundColor <- ConsoleColor.Green
            Console.WriteLine("Success!")
            0
    finally
        Console.WriteLine ("Done. Press [Enter] to close.")
        Console.ResetColor ()
