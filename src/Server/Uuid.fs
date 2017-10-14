[<AutoOpen>]
module Common
open System

module private internals =
    // thanks go to tonsky
    let rand = Random(int DateTime.Now.Ticks)
    let encodeTable = "-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz"
    

    let rec encode (n: uint64) ls =
        function
        | 0 -> ls
        | len -> encode (n / 64UL) (int(n % 64UL)::ls) (len - 1)
    let encodeStr n len =
        let digits = encode n [] len
        digits |> List.map (fun i -> encodeTable.[i]) |> List.toArray |> String

    let rec decodeStr a = function
        | [] -> Some a
        | (ch: char)::tail ->
            match encodeTable.IndexOf ch with
            | -1 -> None
            | i -> decodeStr (a * 64UL + (uint64)i) tail

open internals

type Uuid = {i1: int64; i2: int}
with
    static member Empty = {i1 = -1L; i2 = -1}
    static member New() =
        {
            i1 = DateTime.Now.Ticks
            i2 = rand.Next()
        }
    override this.ToString() =
        encodeStr (uint64 this.i1) 10 + encodeStr (uint64 this.i2) 3
    static member TryParse(s: string) =
        let chars = [for c in s -> c]
        if List.length chars = 13 then
            match chars |> List.take 10 |> decodeStr 0UL, chars |> List.skip 10 |> decodeStr 0UL with
            | Some i1, Some i2 -> Some {i1 = int64 i1; i2 = int i2}
            | _ -> None
        else
            None
