namespace FSharpWebApi.Controllers

open System
open System.Collections.Generic
open System.Linq
open System.Threading.Tasks
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open FSharpWebApi
open Microsoft.AspNetCore.Http

[<ApiController>]
[<Route("[controller]")>]
type FileUploadController (logger : ILogger<FileUploadController>) =
    inherit ControllerBase()

    [<HttpPost>]
    member x.Post(files: IFormFileCollection): FileUpload = 
        let names = 
            files |> Seq.map (fun f -> f.Name) |> (fun f -> String.Join (", ", f))
        { Status = names }
        