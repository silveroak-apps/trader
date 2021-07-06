[<AutoOpen>]
module Process

open System
open Serilog
open System.Diagnostics
open Serilog.Context

let rec repeatEveryIntervalWhile (shouldContinue : unit -> bool) (interval: TimeSpan) (fn: unit -> Async<unit>) (nameForLogging: string)  =
    async {
        use _ = LogContext.PushProperty("Function", nameForLogging)
        try
            let sw = Stopwatch.StartNew ()
            do! fn ()
            sw.Stop ()
            // Log.Verbose ("{Function} took {TimerFunctionDuration} milliseconds", nameForLogging, sw.Elapsed.TotalMilliseconds)
        with e -> Log.Warning (e, "Error running function {Function} on timer. Continuing next time...", nameForLogging)
        
        do! Async.Sleep (int interval.TotalMilliseconds)
        
        if shouldContinue () then
            do! repeatEveryIntervalWhile shouldContinue interval fn nameForLogging 
    }

let rec repeatEvery = repeatEveryIntervalWhile (fun () -> true)

let rec private retryForErrorResult<'TIn, 'TOut, 'TError> attempt maxCount (delay: TimeSpan option) (functionName: string) (input: 'TIn) (isRetryable: 'TError -> bool) (fn: 'TIn -> Async<Result<'TOut, 'TError>>) =
    async {
        use _ = LogContext.PushProperty("Function", functionName)
        let! result = fn input
        match result with
        | Error e ->
            if isRetryable e 
            then
                Log.Warning("Error running function {Function} with retry. Attempt {attempt}/{maxCount}. {Error}", functionName, attempt, maxCount, e)
                if attempt < maxCount
                then 
                    match delay with
                    | Some ts -> do! Async.Sleep (ts.TotalMilliseconds |> int)
                    | None -> ()
                    let! result = (retryForErrorResult<'TIn, 'TOut, 'TError> (attempt + 1) maxCount delay functionName input isRetryable fn)
                    return result
                else 
                    return result
            else
                return result

        | _ -> return result
    }

// TODOLATER might be a good idea to look at Polly ?
let rec private retry'<'TIn, 'TOut> attempt maxCount (delay: TimeSpan option) (shouldRetry: exn -> bool) (p: 'TIn) (fn: 'TIn -> Async<'TOut>) =
    async {
        try
            return! fn p
        with e -> 
            Log.Warning(e, "Error running function with retry. Attempt {attempt}/{maxCount}.", attempt, maxCount)
            if attempt < maxCount
            then 
                match delay with
                | Some ts -> do! Async.Sleep (ts.TotalMilliseconds |> int)
                | None -> ()

                if (shouldRetry e) then
                    return! (retry'<'TIn, 'TOut> (attempt + 1) maxCount delay shouldRetry p fn)
                else
                    raise e
                    return Unchecked.defaultof<'TOut> // only to keep the compiler happy - the previous `raise e` will always cause an exception
            else 
                raise e
                return Unchecked.defaultof<'TOut> // only to keep the compiler happy - the previous `raise e` will always cause an exception
    }

let private shouldAlwaysRetryForException _ = true

let withConditionalRetry'<'TIn, 'TOut> count = retry'<'TIn, 'TOut> 0 count (Some <| TimeSpan.FromSeconds 1.0)

let withRetry<'T> count = retry'<unit, 'T> 0 count (Some <| TimeSpan.FromSeconds 1.0) shouldAlwaysRetryForException ()

let withRetry'<'TIn, 'TOut> count = retry'<'TIn, 'TOut> 0 count (Some <| TimeSpan.FromSeconds 1.0) shouldAlwaysRetryForException

let withRetryOnErrorResult<'TIn, 'TOut, 'TError> count delay = retryForErrorResult<'TIn, 'TOut, 'TError> 0 count (Some delay)

let isAlwaysRetryable _ = true
let isNeverRetryable _ = false

let startHeartbeat name =
    repeatEvery (TimeSpan.FromSeconds(5.0)) (fun _ -> Db.Common.saveHeartbeat name) (sprintf "Heartbeat - %s" name)
    |> Async.Start
        