module Tests

open System
open Xunit
open Shared
open Shared.Player
open Shared.Board
open Swensen.Unquote

let (=>) events cmd =
    events
    |> List.fold Board.evolve Board.initialState
    |> Board.decide cmd

let (==) = (=)

[<Fact>]
let ``IsInField corner``() =
    let field =
        set [ Parcel Axe.NE; Parcel Axe.SE]
        |> Field

    test <@ Crossroad (2 * Axe.SE, CLeft) |> Crossroad.isInField field @>


[<Fact>]
let ``IsInField border``() =
    let field =
        set [ Parcel Axe.NE; Parcel Axe.SE]
        |> Field

    test <@  Crossroad (Axe.center, CRight) |> Crossroad.isInField field @>

[<Fact>]
let ``IsInField inside``() =
    let field =
        set [ Parcel.center; Parcel Axe.NE; Parcel Axe.SE]
        |> Field

    test <@ Crossroad (Axe.center, CRight) |> Crossroad.isInField field @>
    
[<Fact>]
let ``not IsInField``() =
    let field =
        set [ Parcel Axe.NE; Parcel Axe.SE]
        |> Field

    test <@ not (Crossroad (2 * Axe.SE, CRight) |> Crossroad.isInField field) @>


[<Fact>]
let ``Path of moves``() =

    let path =
        Crossroad (Axe.center,CRight)
        |> Path.ofMoves [ Horizontal; Up; Up; Up; Horizontal; Down ]

    test <@ path = ([ Path(Axe.SE,BN), Horizontal
                      Path(Axe.E2,BNW), Up
                      Path(Axe.NE,BNE), Up
                      Path(2*Axe.NE, BNW), Up
                      Path(2*Axe.NE, BN), Horizontal
                      Path(2*Axe.NE, BNE), Down ],
                    Crossroad(2*Axe.NE, CRight))  @>

//[<Fact>]
//let ``Fence move``() =
//    test <@
//            Player.start Blue (Parcel Axe.center) (Crossroad (Axe.center, CLeft))
//            |> Player.move Up
//            |> Player.move Up
//            |> Player.move Horizontal
//            |> Player.move Down
//             =  Playing
//                { Color = Blue
//                  Tractor = Crossroad(Axe.NW, CLeft)
//                  Fence = Fence [ Path (Axe.NW, BNW), Down
//                                  Path (Axe.NW, BN), Horizontal
//                                  Path(Axe.NW, BNE), Up ]
//                  Field = Field.create (Parcel Axe.center)
//                  Moves = { Capacity = 0; Done = 0 }
//                  Hand = Hand.Public []
//                  Watched = false
//                  HighVoltage = false
//                  Power = PowerUp}

//                      @>


//[<Fact>]
//let ``Fence move reverse``() =
//    test <@
//            Player.start Blue (Parcel Axe.center) (Crossroad (Axe.center, CLeft))
//            |> Player.move Up
//            |> Player.move Up
//            |> Player.move Horizontal
//            |> Player.move Down
//            |> Player.move Up
//            |> Player.move Horizontal
//             =  Playing
//                { Color = Blue
//                  Tractor = Crossroad(Axe.N, CLeft)
//                  Fence = Fence [ Path (Axe.NW, BNE), Up ]
//                  Field = Field.create (Parcel Axe.center)
//                  Moves = { Capacity = 0; Done = 0 }
//                  Hand = Hand.Public []
//                  Watched = false
//                  HighVoltage = false
//                  Power = PowerUp} @>


//[<Fact>]
//let ``Loops are deleted``() =
//    test <@
//            Player.start Blue (Parcel Axe.center) (Crossroad (Axe.center, CRight))
//            |> Player.move Horizontal
//            |> Player.move Up
//            |> Player.move Horizontal
//            |> Player.move Down
//            |> Player.move Down
//            |> Player.move Horizontal
//            |> Player.move Up
//             =  Playing
//                { Color = Blue
//                  Tractor = Crossroad(Axe.E2, CLeft)
//                  Fence = Fence [ Path (Axe.SE, BN), Horizontal ]
//                  Field = Field.create (Parcel Axe.center)
//                  Moves = { Capacity = 0; Done = 0 }
//                  Hand = Hand.Public []
//                  Watched = false
//                  HighVoltage = false
//                  Power = PowerUp} @>
let started =
    Board.Started {
        Players = [ Blue,"p1","p1", Parcel.center + 2 * Axe.N
                    Yellow, "p2","p2", Parcel.center + 2 * Axe.S ]
        DrawPile = []
        Barns = []
        Goal = Goal.Common 3
    }

let left axe = Crossroad (axe,CLeft) 
let right axe = Crossroad (axe, CRight)
let pN axe = Path(axe,BN)
let pNE axe = Path(axe,BNE)
let pNW axe = Path(axe,BNW)

let played p e =  Board.Played(p,e)
let play p e =  Board.Play(p,e)

let p1 = played "p1"
let p2 = played "p2"

let cmd1 =  play "p1"
let cmd2 =  play "p2"

let fencesDrawn p start dirs =
    dirs
    |> List.mapFold 
        (fun c dir -> 
            let dest = Crossroad.neighbor dir c
            let path = Path.neighbor dir c
            let e = p <| Player.FenceDrawn {Move = dir; Path = path; Crossroad =dest }
            e,dest
        ) start

let startfences p start dirs =
    p (Player.FirstCrossroadSelected { Crossroad = start })
    :: fst (fencesDrawn p start dirs)

let fence start dirs =
    dirs
       |> List.fold 
           (fun (c,f) dir -> 
               let dest = Crossroad.neighbor dir c
               let path = Path.neighbor dir c
               let newFence = (path, dir) :: f
               dest, newFence
           ) (start, [])
    |> snd
    |> List.rev
    |> Fence
     
open Axe

[<Fact>]
let ``Loops are deleted``() =
    let events = 
        [ started
          yield! startfences p1 (left (N+NE)) 
                    [ Down; Horizontal; Down; Down; Horizontal; Up]
          Next
          Next ]
         => cmd1 (Move { Direction = Up; Destination = right N})
    test 
        <@
         events
         == [ p1 <| FenceLooped { Move = Up
                                  Crossroad = right N
                                  Loop = fence (right N) [Horizontal; Down; Down; Horizontal; Up]  } ]

        @>


[<Fact>]
let ``Find boder``() =

    let field = Field.ofParcels [ Parcel.center; Parcel.center + Axe.NE ]

    let start = Crossroad(Axe.SW, CRight)

    let end' = Crossroad(Axe.E2, CLeft) 

    let border =
        Field.borderBetween start end' field

    test <@ border = [ Path(Axe.S,BN), Horizontal
                       Path(Axe.SE,BNW), Up
                       Path(Axe.SE, BN), Horizontal ] @>
    
[<Fact>]
let ``Find boder other direction``() =

    let field = Field.ofParcels [ Parcel.center; Parcel.center + Axe.NE ]

    let end' = Crossroad(Axe.SW, CRight)

    let start = Crossroad(Axe.E2, CLeft) 

    let border =
        Field.borderBetween start end' field

    test <@ border = [ Path(Axe.E2,BNW), Up
                       Path(Axe.NE,BNE), Up
                       Path(Axe.NE,BN), Horizontal
                       Path(Axe.NE,BNW), Down
                       Path(Axe.center, BN), Horizontal
                       Path(Axe.center, BNW), Down
                       Path(Axe.SW, BNE), Down ] @>
[<Fact>]
let ``To oriented``() =

    let start = Crossroad(Axe.SW, CRight)

    let paths, end' = Path.ofMoves [Down; Down; Horizontal; Up; Horizontal; Up ; Up] start

    let fence = Fence(List.rev paths)

    let orienteds,_ = Fence.toOriented end' fence 

    test <@ orienteds = [ DSW; DSE; DE; DNE; DE; DNE; DNW ] @>

[<Fact>]
let ``Fill path``() =
    let  start = Crossroad(Axe.SW, CRight)


    let paths,end' = Path.ofMoves [Down; Down; Horizontal; Up; Horizontal; Up ; Up; Up; Up; Horizontal; Down; Horizontal; Down; Down] start

    let field = Field.fill paths

    test <@
            start = end'
            && field = Field(set [ Parcel.center
                                   Parcel.center + Axe.NE
                                   Parcel.center + Axe.SE
                                   Parcel.center + Axe.S]) @>



