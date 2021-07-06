[<AutoOpen>]
module Config

open Microsoft.Extensions.Configuration
open System.IO
open System
open Newtonsoft.Json
open Newtonsoft.Json.Serialization
open Serilog

let getDefaultSerialisationSettings = 
    let settings = JsonSerializerSettings (
                        ContractResolver =
                            DefaultContractResolver (NamingStrategy = CamelCaseNamingStrategy())
                   )
    Func<_>(fun () -> settings)

JsonConvert.DefaultSettings <- getDefaultSerialisationSettings

let appConfig =
    (new ConfigurationBuilder())
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional = false, reloadOnChange = true)
        .AddEnvironmentVariables()
        .Build()

let connectionStringWithName key = 
    let cnnStringsSection = appConfig.GetSection("ConnectionStrings")
    cnnStringsSection.Item key

let pgsqlConnectionString = connectionStringWithName "PostgresConnection"

let bidsConnectionString = connectionStringWithName "PostgresConnection.BidsSource"