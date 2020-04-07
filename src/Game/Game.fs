module Client

open Elmish
open Elmish.React
open Elmish.Bridge
open Fable.React
open Fable.React.Props
open Fetch.Types
open Thoth.Fetch
open Fulma
open Thoth.Json

open Shared

// The model holds data that you want to keep track of while the application is running
// in this case, we are keeping track of a counter
// we mark it as optional, because initially it will not be available from the client
// the initial value will be requested from server
type Model = {
    Board: Board
    PlayerId: string option
    Moves: Move list 
    Message: string
    }

// The Msg type defines what events/actions can occur while the application is running
// the state of the application changes *only* in reaction to these events
type Msg =
    | Move of Direction
    | SelectFirstCrossroad of Crossroad
    | Remote of ClientMsg
    | ConnectionLost

let parseGame (s:string) =
    let idx = s.LastIndexOf("/")
    s.Substring(idx+1)

// defines the initial state and initial command (= side-effect) of the application
let init () : Model * Cmd<Msg> =
    { Board = { Players = Map.empty }
      Moves = []
      PlayerId = None
      Message = "Init from client"
    }, Cmd.none
// The update function computes the next state of the application based on the current state and the incoming events/messages
// It can also run side-effects (encoded as commands) like calling the server via Http.
// these commands in turn, can dispatch messages to which the update function will react.
let update (msg : Msg) (currentModel : Model) : Model * Cmd<Msg> =
    match msg with
    | SelectFirstCrossroad c ->
        let move = Player.SelectFirstCrossroad { Crossroad = c }
        currentModel, Cmd.bridgeSend(Command move)

    | Move dir ->
        let move = Player.Move { Direction = dir}
        currentModel, Cmd.bridgeSend(Command move)
    | ConnectionLost ->
        { currentModel with Message = "Connection lost"}, Cmd.none
    | Remote (SyncPlayer playerid) ->
        let loc = Browser.Dom.document.location
        { currentModel with PlayerId = Some playerid }, Cmd.bridgeSend (JoinGame (parseGame loc.pathname ))
        
    | Remote (Sync ( s)) ->
        let state = Board.ofState s
        let newState =
            { currentModel with
                  Board = state
                  Moves =  Board.possibleMoves currentModel.PlayerId state
                  }

        //let cmd =
        //    match currentModel.Color with
        //    | Some color -> Cmd.ofMsg (InitColor color)
        //    | None ->
        //        let loc = Browser.Dom.document.location
        //        Cmd.ofMsg (InitColor (parseColor loc.hash ))
            
        newState, Cmd.none// cmd
    //| Remote (SyncColor color) ->
    //    let newState =
    //        { currentModel with
    //              Color = Some color 
    //              Moves =  Board.possibleMoves (Some color) currentModel.Board
    //              Message = "SyncColor" }
    //    newState, Cmd.none

    | Remote (Events e) ->
        let newState = List.fold Board.evolve currentModel.Board e
        { currentModel with
              Board = newState
              Moves = Board.possibleMoves currentModel.PlayerId newState
              Message = "Event"
        }, Cmd.none
    | Remote (Message m) ->
        { currentModel with
              Message = m
        }, Cmd.none


let imgUrl typ color = 
    let colorName = 
        match color with
        | Blue -> "blue"
        | Yellow -> "yellow"
        | Purple -> "purple"
        | Red -> "red"
    sprintf "url('/img/%s-%s.png')" typ colorName
let tileUrl = imgUrl "tile"
    
let playerUrl = imgUrl "player"
let fenceUrl = imgUrl "fence"

let parcel color (Parcel pos) = 
    let x,y = Pix.ofTile pos |> Pix.rotate
    div [ Style [ BackgroundImage (tileUrl color)
                  BackgroundSize "contain"
                  Width "9vw"
                  Height "9vw"
                  Position PositionOptions.Absolute
                  Left (sprintf "%fvw" x)
                  Top (sprintf "%fvw" y)
                   ]] 
        []


let player color pos =
    let x,y = Pix.ofPlayer pos |> Pix.rotate
    div [ Style [ BackgroundImage (playerUrl color)
                  BackgroundSize "contain"
                  Width "2.5vw"
                  Height "2.5vw"
                  Position PositionOptions.Absolute
                  Left (sprintf "%fvw" x)
                  Top (sprintf "%fvw" y)
                   ]] 
        []

let crossroad pos f =
    let x,y = Pix.ofPlayer pos |> Pix.rotate
    div [ Style [ BackgroundSize "contain"
                  Width "2.5vw"
                  Height "2.5vw"
                  Position PositionOptions.Absolute
                  Left (sprintf "%fvw" x)
                  Top (sprintf "%fvw" y)
                   ]
          OnClick f
                   ] 
        []

let blockedCrossroad pos e =
    let x,y = Pix.ofPlayer pos |> Pix.rotate
    div [ Style [ BackgroundSize "contain"
                  Width "2.5vw"
                  Height "2.5vw"
                  Position PositionOptions.Absolute
                  Left (sprintf "%fvw" x)
                  Top (sprintf "%fvw" y)
                  
                   ]
                   ] 
        []

let singleFence color path =
    let x,y = Pix.ofFence path |> Pix.rotate
    let rot =
        match path with
        | Path(_,BN) -> "rotate(4deg)"
        | Path(_,BNW) -> "rotate(-56deg)"
        | Path(_,BNE) -> "rotate(64deg)"

    
    div [ Style [ BackgroundImage (fenceUrl color)
                  BackgroundSize "contain"
                  BackgroundRepeat "no-repeat"
                  Width "4.3vw"
                  Height "1.1vw"
                  Transform rot
                
                  Position PositionOptions.Absolute
                  Left (sprintf "%fvw" x)
                  Top (sprintf "%fvw" y)
                   ]] 
        []


let playerView userId p =
    match p with
    | Playing p ->
        let (Field parcels) = p.Field
        let (Fence paths) = p.Fence
        let color = p.Color
        [
            for p in parcels do
                parcel color p

            for p,_ in paths do
                singleFence color p

            player color p.Tractor
        ]
    | Starting { Parcel = Parcel p; Color = color } ->
        let x,y = Pix.ofTile p |> Pix.rotate
        [
            parcel color (Parcel p)
            div [ Style [ BackgroundImage (playerUrl color)
                          BackgroundSize "contain"
                          Width "2.5vw"
                          Height "2.5vw"
                          Position PositionOptions.Absolute
                          Left (sprintf "%fvw" (x+3.3))
                          Top (sprintf "%fvw" (y+3.3))
                           ]] 
                []
        ]



let moveView dispatch move =
    match move with
    | Move.Move (dir,c) -> crossroad c (fun _ -> dispatch (Move dir))
    | Move.ImpossibleMove (_,c,e) -> blockedCrossroad   c e
    | Move.SelectCrossroad(c) -> crossroad c (fun _ -> dispatch (SelectFirstCrossroad c))
    
    
let view (model : Model) (dispatch : Msg -> unit) =
    div [ Style [ BackgroundImage "url(/img/board.jpg)"
                  Width "100vw"
                  Height "100vw"
                  BackgroundSize "contain"
                  ] ]
        [
            for color,p in Map.toSeq model.Board.Players do
                yield! playerView color p

            for m in model.Moves do
                moveView dispatch m



            //div [ Style [ Position PositionOptions.Fixed
            //              Top 0
            //              Left 0
            //              BackgroundColor "white"
            //              ]
            //]  [   str (sprintf "%A %s" model.Color model.Message) ]

            //for q in -4..4 do
            // for r in -4..4 do
            //    let px,py = Pix.ofTile (Axe(q,r))
            //    let x,y,z = Axe.cube (Axe(q,r))
            //    div [ Style [ Position PositionOptions.Absolute
            //                  Left (sprintf "%fvw" (px+2.5))
            //                  Top (sprintf "%fvw" (py+2.5))
            //                  BackgroundColor "white"
            //                   ]] 
            //        [ str (sprintf "%d,%d,%d" x y z) ]
        ]



#if DEBUG
open Elmish.Debug
open Elmish.HMR
#endif

Program.mkProgram init update view
|> Program.withBridgeConfig
    (
        Bridge.endpoint "/socket/init"
        |> Bridge.withMapping Remote
        |> Bridge.withRetryTime 1
        |> Bridge.withWhenDown ConnectionLost
    )
#if DEBUG
|> Program.withConsoleTrace
#endif
|> Program.withReactBatched "elmish-app"
#if DEBUG
|> Program.withDebugger
#endif
|> Program.run

        

