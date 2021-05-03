module DapperInfra

open Dapper
open System

type OptionHandler<'T>() =
    inherit SqlMapper.TypeHandler<option<'T>>()

    override __.SetValue(param, value) = 
        let valueOrNull = 
            match value with
            | Some x -> box x
            | None -> null

        param.Value <- valueOrNull    

    override __.Parse value =
        if isNull value || value = box DBNull.Value 
        then None
        else Some (value :?> 'T)

let setup () =
    SqlMapper.AddTypeHandler (OptionHandler<string>())
    SqlMapper.AddTypeHandler (OptionHandler<int>())
    SqlMapper.AddTypeHandler (OptionHandler<DateTime>())
    SqlMapper.AddTypeHandler (OptionHandler<decimal>())