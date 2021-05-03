module ResultEx

let combineList (r1: Result<'a list, 'b>) (r2: Result<'a, 'b>) =
    match r1, r2 with
    | Ok a, Ok b -> b :: a |> Ok
    | Error s, _ -> Error s
    | _, Error s -> Error s
