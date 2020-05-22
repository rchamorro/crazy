module SharedClient

open Shared
open Fable.React
open Fable.React.Props
open Elmish.React
open Globalization

type CardExt =
| NoExt
| FirstHayBale of bool * Path
| Hidden 

type CardAction = 
    { Index: int
      Card: Card
      Ext: CardExt }


type Chat =
    { Entries: ChatEntry list 
      Show: bool
      Pop: bool }

// The model holds data that you want to keep track of while the application is running
// in this case, we are keeping track of a counter
// we mark it as optional, because initially it will not be available from the client
// the initial value will be requested from server
type Model =
  { Board: Board
    LocalVersion: int
    Synched: Board
    Version: int
    PlayerId: string option
    CardAction: CardAction option
    Moves: Move list 
    Message: string
    Error : string
    DashboardOpen: bool
    PlayedCard: Card option
    Chat: Chat
    }


// The Msg type defines what events/actions can occur while the application is running
// the state of the application changes *only* in reaction to these events
type Msg =
    | Move of Direction * Crossroad
    | PlayCard of PlayCard
    | SelectFirstCrossroad of Crossroad
    | SelectCard of Card * int
    | SelectHayBale of Path
    | CancelCard
    | SwitchDashboard
    | EndTurn
    | Remote of ClientMsg
    | ConnectionLost
    | Go
    | HidePlayedCard
    | SendMessage of string
    | ToggleChat
    | HidePop



module Pix =
    let size = 5.75
    let offsetX = 48.5
    let offsetY = 47.8

    let ofPoint (Axe(q,r)) = 
        let x = size * (3./2. * float q ) + offsetX
        let y = size * (sqrt 3./2. * float q  +  sqrt 3. * float r) + offsetY
        x,y

    let ofTile pos =
        let x,y = ofPoint pos  
        x - size /2. , y - size /2.

    let playerSize = 0.
    let ofPlayer (Crossroad (tile,corner)) =
        let tx,ty = ofPoint tile 
        let x = 
            match corner with
            | CLeft  -> tx - size * 0.9 - playerSize/2.
            | CRight -> tx + size * 1.1 - playerSize/2.
        let y = ty - playerSize/2.
        x,y
          

    let fenceWidth = 2.
    let ofFence (Path(tile,border)) =
        let tx,ty = ofPoint tile
        
        match border with
        | BN -> tx - size * 0.1,ty-size*0.73
        | BNW -> tx - size*0.77,ty-size*0.31
        | BNE -> tx + size*0.74,ty-size*0.27



    let teta = -0.063
    let cost = cos teta 
    let sint = sin teta
    let rotate (x,y) =
        let cx = x-offsetX
        let cy = y-offsetY
        offsetX + cx * cost + cy * sint, offsetY - cx * sint + cy * cost 

    let translate (dx: float,dy: float) (x: float,y: float) =
        x+dx, y+dy




let colorName color = 
    match color with
    | Blue -> "blue"
    | Yellow -> "yellow"
    | Purple -> "purple"
    | Red -> "red"
    
let translatedColorName color = 
    match color with
    | Blue -> translate "blue"
    | Yellow -> translate "yellow"
    | Purple -> translate "purple"
    | Red -> translate "red"
    

let parcel (Parcel pos) attr = 
    let x,y = Pix.ofTile pos |> Pix.rotate
    div ([ ClassName "tile"
           Style [ Left (sprintf "%f%%" x)
                   Top (sprintf "%f%%" y)
                   ]]  @ attr)
          
        []
let tile (Parcel pos) f = 
    let x,y = Pix.ofTile pos |> Pix.rotate
    div ([ ClassName "tile"
           Style [ Left (sprintf "%f%%" x)
                   Top (sprintf "%f%%" y)
                   ]
           OnClick f
                   ]   )
          
        []

let barn (Parcel pos) occupied = 
    let x,y = Pix.ofTile pos |> Pix.rotate
    div [ classBaseList "barn" [ "occupied", occupied ]
          Style [ Left (sprintf "%f%%" x)
                  Top (sprintf "%f%%" y)
                   ]] 
        []

let drawplayer selected (x,y) =
    div [ classBaseList "player" ["selected", selected]
          Style [ Left (sprintf "%f%%" x)
                  Top (sprintf "%f%%" y)
                   ]] 
        []

let player active pos =
    Pix.ofPlayer pos
    |> Pix.rotate
    |> drawplayer active


let tooltip txt =
    div [ ClassName "tooltiptext"] [ str txt ]
let drawcrossroad pos f =
    let x,y = Pix.ofPlayer pos |> Pix.rotate
    div [ ClassName "crossroad"
          Style [ Left (sprintf "%f%%" x)
                  Top (sprintf "%f%%" y) ]
          match f with
          | Ok f -> OnClick f
          | _ -> () ] 
        [ match f with
          | Ok _ -> ()
          | Error Tractor -> tooltip (translate "The crossroad is occupied by another tractor")
          | Error HayBaleOnPath -> tooltip (translate "The path is blocked by a hay bale")
          | Error PhytosanitaryProducts -> tooltip (translate "You cannot cut a fence just after using an Helicopter: with phytosanitary products, it could explode")
          | Error Protection -> tooltip (translate "You cannot cut on the two fences behind a tractor. Too close, you'd be shotgunned !")
          | Error HighVoltageProtection -> tooltip (translate "The fence is under High Voltage. You'd be reduced to ashes !")
          
                
        ]

let crossroad pos f = drawcrossroad pos (Ok f)

let blockedCrossroad pos e = drawcrossroad pos (Error e)

let singleFence path =
    let x,y = Pix.ofFence path |> Pix.rotate
    let rot =
        match path with
        | Path(_,BN) -> "rotate(4deg)"
        | Path(_,BNW) -> "rotate(-56deg)"
        | Path(_,BNE) -> "rotate(64deg)"

    
    div [ ClassName "fence"
          Style [ Transform rot
                  Left (sprintf "%f%%" x)
                  Top (sprintf "%f%%" y) ]] 
        []

let haybale p =
    let x,y = Pix.ofFence p |> Pix.translate (0.2,-0.8) |> Pix.rotate
    div [ ClassName "hay-bale"
          Style [ Left (sprintf "%f%%" x)
                  Top (sprintf "%f%%" y) ]
          ]
        []

let path p f =
    let x,y = Pix.ofFence p |> Pix.translate (0.2,-0.8) |> Pix.rotate
    div [ ClassName "path"
          Style [ Left (sprintf "%f%%" x)
                  Top (sprintf "%f%%" y) ]
          OnClick f

          ]
        []


let sameField x y =
    match x,y with
    | Playing px, Playing py -> px.Field = py.Field
    | Starting px, Starting py -> px = py 
    | _ -> false

let sameFence x y = 
    match x,y with
    | Playing px, Playing py -> px.Fence = py.Fence
    | Starting _, Starting _ -> true
    | _ -> false

let playerField  p =
        match p with
        | Playing p ->
            let principal =
                Field.principalField p.Field p.Fence p.Tractor
            let fallow = p.Field - principal

            div [ ClassName (colorName p.Color)]
                [ 
                  for p in Field.parcels principal do
                        parcel p []
                 
                  div [ ClassName "fallow" ]
                      [ for p in Field.parcels fallow do
                          parcel p [] ]
                        
                ]
        | Starting { Parcel = p; Color = color } ->
            div [ ClassName (colorName color) ]
                [ parcel p [] ] 
        | Ko _ -> 
            null

let playerFences  p =
    match p with
    | Playing p ->
        div [ ClassName (colorName p.Color) ]
            [ let (Fence paths) = p.Fence
              for p,_ in paths do
                singleFence p ]
    | Starting _ 
    | Ko _ ->
        null

let playerTractor selected p =
    match p with
    | Playing p ->
        div [ ClassName (colorName p.Color) ]
            [ player selected p.Tractor ]
    | Starting { Parcel = Parcel p; Color = color } ->
        div [ ClassName (colorName color) ]
            [ Pix.ofTile p
              |> Pix.rotate
              |> Pix.translate (3.3,3.3)
              |> drawplayer selected  ]
    | Ko _ -> null



let moveView dispatch move =
    match move with
    | Move.Move (dir,c) -> crossroad c (fun _ -> dispatch (Move (dir, c)))
    | Move.ImpossibleMove (_,c,e) -> blockedCrossroad   c e
    | Move.SelectCrossroad(c) -> crossroad c (fun _ -> dispatch (SelectFirstCrossroad c))

       

let handView dispatch title playerId board cardAction hand =
    let player = Board.currentPlayer board
    let active =  Option.exists (fun p -> p = board.Table.Player) playerId
    let otherPlayers = Board.currentOtherPlayers board
    let cancel = a [ ClassName "cancel"; Href "#"; OnClick (fun _ -> dispatch CancelCard)] [ str (translate "Cancel") ]
    let go = button [ OnClick (fun _ -> dispatch Go )]  [str (translate "Go") ]
    let action title texts buttons =
        div [ClassName "action" ]
            [ h2 [] [ str title ]
              for t in texts do
                p [] [ t ]

              if active then
                  div [ ClassName "buttons" ] [ yield! buttons; yield cancel ] 
              else
                  div [ ClassName "buttons" ]
                    [ str (translate ("You can only play cards during your turn."))
                      cancel ]
            ]

    match hand with 
    | PublicHand cards -> 
        div [ ClassName "cards" ]
            [ 
              if title then
                  div [ ClassName "hand-title"] [ str (translate ("Your hand")) ]

              if title && List.isEmpty cards then
                  div [ ClassName "empty-hand"] [ 
                    p [] [ str (translate "Your hand is empty") ]
                    p [] [ str (translate "Take over barns to get bonus cards") ]  ]
              else
                  for i,c in List.indexed cards do
                   div [ClassName "card-container" ]
                       [
                            div [ ClassName ("card " + Client.cardName c)
                                  match cardAction with
                                  | Some { Index = index } when index = i -> 
                                        OnClick (fun _ -> dispatch CancelCard)
                                  | _ -> OnClick (fun _ -> dispatch (SelectCard(c,i))) ] []

                            match cardAction with
                            | Some { Index = index; Card = Nitro power; Ext = NoExt} when index = i ->
                                action ( match power with One -> translate "Nitro +1" | Two -> translate  "Nitro +2") 
                                    [ str (sprintf (translate "Gives you %d extra move(s) during this turn.") (match power with One -> 1 | Two -> 2 ))
                                      Standard.i [] [str (translate "(Reminder: max. 5 moves per turn)") ] ]
                                     [ button [ OnClick (fun _ -> dispatch (PlayCard (PlayNitro power))) ] [ str (translate "Play") ] ]
                            | Some { Index = index; Card = Rut; Ext = NoExt } when index = i ->
                                action (translate "Rut")
                                    [ str (translate "Choose an opponent; he/she will have two fewer moves during his next turn") ]
                                    [ for playerId, player in otherPlayers do
                                          div [ OnClick (fun _ -> dispatch (PlayCard (PlayRut playerId)))
                                                ClassName (colorName (Player.color player)) ] [
                                                    div [ ClassName "player"] [] ] ]
                            | Some { Index = index; Card = HighVoltage; Ext = NoExt } when index = i ->
                                action (translate "High Voltage")
                                    [ str (translate "Protects the entire length of the fence, even the starting point until your next turn. Other tractors cannot go through or cut your fence.") ]
                                    [ button [ OnClick (fun _ -> dispatch (PlayCard (PlayHighVoltage))) ] [ str (translate "Play") ] ]
                            | Some { Index = index; Card = Watchdog; Ext = NoExt } when index = i ->
                                action (translate "Watchdog")
                                    [ str (translate "Protects your plots and barns from being annexed until next turn. Annexations by opponents leave your plots and barns in place.") ]
                                    [ button [ OnClick (fun _ -> dispatch (PlayCard (PlayWatchdog))) ] [ str (translate "Play") ] ] 
                            | Some { Index = index; Card = Helicopter; Ext = NoExt } when index = i ->
                                let canUseHelicopter = Player.canUseHelicopter player
                                action (translate "Helicopter")
                                    [ str (translate "Moves your tractor to any point in your field. The point of arrival must be in the field or at the edge. Once moved, you cannot cut any more fences until the end of the turn: crop protection agents + electicity... I could explode!")
                                      if canUseHelicopter then
                                        str (translate "Select a destination in your field")
                                      else
                                        str (translate "Cannot be played with a fence") ]
                                    [ if canUseHelicopter then
                                        go ]
                            | Some { Index = index; Card = HayBale One; Ext = NoExt } when index = i ->
                                action (translate "1 Hay Bale")
                                    [ str (translate "Hay bales block the path for all players until blasted out with dynamite. You cannot place a Hay Bale on a fence in progress or on the edge of the board. It is forbiddent to lock in an opponent.")
                                      str (translate "Select a free path for the hay bale") ]
                                    [ go ]
                            | Some { Index = index; Card = HayBale Two; Ext = NoExt } when index = i ->
                                action (translate "2 Hay Bales")
                                    [ str (translate "Hay bales block the path for all players until blasted out with dynamite. You cannot place a Hay Bale on a fence in progress or on the edge of the board. It is forbiddent to lock in an opponent.")
                                      str (translate "Select a  free paths for the first hay bale") ]
                                    [ go ]
                            | Some { Index = index; Card = HayBale Two; Ext = FirstHayBale (false,_) } when index = i ->
                                 action "2 Hay Bales"
                                    [ str "Hay bales block the path for all players until blasted out with dynamite. You cannot place a Hay Bale on a fence in progress or on the edge of the board. It is forbiddent to lock in an opponent."
                                      str "Select a free paths for the second hay bales" ] 
                                    [ go ]
                            | Some { Index = index; Card = Dynamite; Ext = NoExt } when index = i ->
                                 action (translate "Dynamite")
                                    [ str (translate "Remove 1 Hay Bale of your choice")
                                      str (translate "Select a hay bale to blow up") ]
                                    [ go ]
                            | Some { Index = index; Card =  Bribe; Ext = NoExt } when index = i ->
                                 action (translate "Bribe")
                                    [ str (translate "It wasn't clear on the plan... slipping a small bill should do the trick. The choose a plot of an opponent's field that has a common edge with yours... now it belongs to you! Careful, it needs to be discreet. You cannot take a plot of land from which a fence starts, it would cut it off, hence a bit conspicuous... You cannot take a barn either, hard to hide... You cannot place your last plot using this bonus, it would be a bit much!")
                                      match Board.bribeParcels board with
                                      | Ok _ -> str (translate "Select a parcel on the border of your field to take over")
                                      | Error Board.InstantVictory -> str (translate "You cannot bribe to take the last parcel ! That would be too visible !")
                                      | Error Board.NoParcelsToBribe -> str (translate "There is no parcel to bribe.")
                                    ]
                                    [ go ]
                            | _ -> ()
                        ]
           ]
    | PrivateHand cards ->
        div [ ClassName "cards"]
            [ for c in 1..cards do
               div [ClassName "card-container" ]
                   [ div [ ClassName (sprintf "card back z%d" c) ] [] ]
            ]

let barnsView barns =
    div [] 
        [ for parcel in Field.parcels barns.Free do
            barn parcel false
          for parcel in Field.parcels barns.Occupied do
            barn parcel true ]

let hayBalesView board =
    div []
        [ for p in board.HayBales do
            haybale p
        ]

let boardView board =
   [ for _,p in Map.toSeq board.Players do
         playerField p

     lazyView barnsView board.Barns

     for _,p in Map.toSeq board.Players do
         lazyViewWith sameFence playerFences  p

     for playerid,p in Map.toSeq board.Players do
         playerTractor (Table.isCurrent playerid board.Table)  p
         
     hayBalesView board ]

let goalView board =
    match board.Goal with
    | Common c ->
        div []
            [ str (sprintf (translate "%d parcels left") (c - Board.totalSize board)) ]
    | Individual c ->
        div []
            [ for playerid, player in Map.toSeq board.Players do
                p [] [ str (sprintf (translate "%s: %d parcels left") board.Table.Names.[playerid] (c - Player.fieldTotalSize player)) ]
            ]

let endTurnView dispatch playerId board =
    if playerId = Some board.Table.Player then
        match  board.Players.[board.Table.Player] with
        | Playing player when 
             not (Moves.canMove player.Moves) && Hand.canPlay player.Hand || List.isEmpty (Board.possibleMoves playerId (Board board)) ->
               [ crossroad player.Tractor (fun _ -> dispatch EndTurn) ]
                
        | _ -> []
    else
        []

let helicopterDestinations player board =
    Field.crossroads (Player.field player) 
    - (set [ for _,p in Map.toSeq board.Players do
                match p with
                | Playing p ->
                      p.Tractor
                      yield! Fence.fenceCrossroads p.Tractor p.Fence
                | _ -> ()
                      ])


let findCutPaths hayBales =
    let neighbor dir crossroad = 
        let neighbor = Crossroad.neighbor dir crossroad
        if Crossroad.isOnBoard neighbor then
            let path = Path.neighbor dir crossroad
            if Set.contains path hayBales then
                None
            else
                Some (path,neighbor)
        else
            None

    let mutable cut = []
    let mutable visited = Map.empty
    let mutable time = 0
    let rec loop parent crossroad : int =
        visited <- Map.add crossroad time visited
        let d0 = time
        time <- time + 1 
        let upDepth =
            match neighbor Up crossroad with
            | Some (p,nxt) when nxt <> parent ->
                let n = 
                    match Map.tryFind nxt visited with
                    | Some d -> d
                    | None -> loop crossroad nxt 
                if n > d0 then
                    cut <- p :: cut 
                n
            | _ -> 
                d0 + 1
        let downDepth = 
            match neighbor Down crossroad with
            | Some (p,nxt) when nxt <> parent ->
                let n = 
                    match Map.tryFind nxt visited with
                    | Some d -> d
                    | None -> loop crossroad nxt 
                if n > d0 then
                    cut <- p :: cut 
                n
            | _ -> 
                d0 + 1
        let horizontalDepth = 
            match neighbor Horizontal crossroad with
            | Some (p,nxt) when nxt <> parent ->
                let n = 
                    match Map.tryFind nxt visited with
                    | Some d -> d
                    | None -> loop crossroad nxt 
                if n > d0 then
                    cut <- p :: cut 
                n
            | _ -> 
                d0 + 1

        let d = min upDepth downDepth |> min horizontalDepth 
        visited <- Map.add crossroad d visited
        d

    let start = Crossroad(Axe.center, CLeft)
    loop start start |> ignore
    set cut

    


let hayBaleDestinations board hayBales =
    Path.allInnerPaths 
        - Set.unionMany 
            [ for KeyValue(_,p) in board.Players do
                p  |> Player.fence |> Fence.fencePaths |> set ]
        - hayBales
        - findCutPaths hayBales


let boardCardActionView dispatch board  cardAction =

    match cardAction with
    | Some { Card = Helicopter} ->
        let player = Board.currentPlayer board
        [ for c in helicopterDestinations player board do
            crossroad c (fun _ -> dispatch (PlayCard (PlayHelicopter c))) ]
    | Some { Card =  HayBale One } ->
        [ for p in hayBaleDestinations board board.HayBales do
            path p (fun _ -> dispatch (PlayCard (PlayHayBale [ p ]))) ]
    | Some {Card = HayBale Two; Ext = (NoExt | Hidden) } ->
        [ for p in hayBaleDestinations board board.HayBales  do
            path p (fun _ -> dispatch (SelectHayBale p) ) ]
    | Some {Card = HayBale Two; Ext = FirstHayBale(_,fp) } ->
        [ haybale fp
          for p in hayBaleDestinations board (Set.add fp board.HayBales ) do
            path p (fun _ -> dispatch (PlayCard (PlayHayBale [fp; p])) ) ]
    | Some { Card =  Dynamite}  ->
        [ for p in board.HayBales do
            path p (fun _ -> dispatch (PlayCard (PlayDynamite p))) ]
    | Some { Card =  Bribe}  ->
        match Board.bribeParcels board with
        | Ok parcels ->
            [ for p in Field.parcels parcels do
                tile p (fun _ -> dispatch (PlayCard (PlayBribe p))) ]
        | Error _ -> []
    | _ ->
        []

let flash active =
    div [ classBaseList "flash" [ "inactive", not active] ]
        []

let bars = i [ ClassName "fas fa-bars"] [
 svg [ AriaHidden true; Role "img"; ViewBox "0 0 448 512" ]
    [ Fable.React.Standard.path [ SVGAttr.Fill "currentColor"; D "M16 132h416c8.837 0 16-7.163 16-16V76c0-8.837-7.163-16-16-16H16C7.163 60 0 67.163 0 76v40c0 8.837 7.163 16 16 16zm0 160h416c8.837 0 16-7.163 16-16v-40c0-8.837-7.163-16-16-16H16c-8.837 0-16 7.163-16 16v40c0 8.837 7.163 16 16 16zm0 160h416c8.837 0 16-7.163 16-16v-40c0-8.837-7.163-16-16-16H16c-8.837 0-16 7.163-16 16v40c0 8.837 7.163 16 16 16z" ] []]
    ]

let bonusMarkers bonus =
    div [ ClassName "markers"]
        [
          for _ in  1 .. bonus.Rutted do
            div [ ClassName "rut-marker" ] []
          for _ in 1 .. bonus.NitroOne do
            div [ ClassName "nitro-1-marker"] []
          for _ in 1 .. bonus.NitroTwo do
            div [ ClassName "nitro-2-marker"] []
          if bonus.HighVoltage then
            div [ ClassName "highvoltage-marker" ] []
          if bonus.Watched then
            div [ ClassName "watchdog-marker" ] []
          if bonus.Heliported > 0 then
            div [ ClassName "helicopter-marker" ] []

        ]

let commonGoal board goal =
   div [ ClassName "common-goal" ]
       [ let colors = [ for KeyValue(_,p) in board.Players -> Player.color p]
         for c in colors do
           div [ ClassName ("stack " + colorName c)]
               [ div [ ClassName ("tile")] [] ]
         div [ClassName "tile-count"]
           [ str (sprintf "x%d" (goal - Board.totalSize board)) ]
       ]

type PlayerInfo = 
    { Name: string option
      Player: CrazyPlayer
      IsActive: bool
      Goal: Goal option
      PlayingBoard: PlayingBoard
    }

let playerInfo info (cards: ReactElement) dispatch =
    div [ ClassName "player-top"]
        [
            let color = Player.color info.Player
            div [ ClassName "description"]
              [ div [ ClassName (colorName color) ]
                    [ div [ classBaseList "player" [ "selected", info.IsActive ; "ko", Player.isKo info.Player ] ] []]
                
               
                match info.Name with
                | Some name -> div [ ClassName "name"] [ str name ] 
                | _ -> ()
                
                div [ ClassName "moves" ] 
                    [ if info.IsActive then
                        match info.Player with
                        | Starting _->
                            for _ in 1 .. 3 do
                                flash false 
                        | Playing p ->
                            for i in 1 .. p.Moves.Capacity do
                                flash  (i <= p.Moves.Done)
                        | Ko _ -> ()
                    ]
                ]

            
            bonusMarkers (Player.bonus info.Player)
            match info.Player, info.Goal with
            | Ko _, _ -> ()
            | _, Some (Individual goal) ->
               div [ ClassName "individual-goal" ]
                   [ div [ ClassName ("stack " + colorName color)]
                         [ div [ ClassName ("tile")] []  ]
                     div [ClassName "tile-count"]
                         [ str (sprintf "x %d" (goal - Player.fieldTotalSize info.Player)) ] 
                   ]
            | _, Some (Common goal) ->
                commonGoal info.PlayingBoard goal
            | _ -> ()
            cards
        ]

      

let playersDashboard model dispatch =
    match model.Board with
    | InitialState -> null
    | Board board
    | Won(_,board) ->

        div [ ClassName "header"] [

            div [ classBaseList "dashboard" [ "closed",not model.DashboardOpen  ] ]
                [  
                    span [ OnClick (fun _ -> dispatch SwitchDashboard)]
                        [ bars ]

                    let currentPlayer = board.Table.Player
                    for playerid in board.Table.AllPlayers do
                        let player = board.Players.[playerid]
                        div[ classBaseList "player-dashboard" [ "local", Some playerid = model.PlayerId ]] [
                            playerInfo { Name = Some(board.Table.Names.[playerid])
                                         Player = player 
                                         IsActive = currentPlayer = playerid
                                         Goal = match board.Goal with | Individual _ as g -> Some g | _ -> None 
                                         PlayingBoard = board } null dispatch

                            if model.DashboardOpen then
                                handView dispatch false model.PlayerId board model.CardAction (Player.hand player)

                            //goalView board
    
                        ]
                    match board.Goal with
                    | Common goal ->
                        commonGoal board goal
                    | _ -> () ]

            if model.DashboardOpen then
                div [ ClassName "help"] [ 
                    let currentPlayer = board.Table.Player
                    let isActive = model.PlayerId.IsSome && currentPlayer = model.PlayerId.Value
                    let player = board.Players.[board.Table.Player]
                    if isActive then
                        match player with
                        | Starting p ->
                            str (sprintf "Let's go ! Select a crossroad around your %s field to start." (translatedColorName p.Color) )
                        | Playing p ->
                            if Moves.canMove p.Moves then
                                if p.Power = PowerDown then
                                    str "You're fence has been cut. Go back to your field to draw a new one. "
                                    match p.Bonus.Rutted with
                                    | 0 -> ()
                                    | 1 ->
                                        str "You're victime of a rut, you lost 2 moves. "
                                    | n ->
                                        str (sprintf "You're victime of %d ruts, you lost %d moves. " n (n*2))
                                elif p.Moves.Done = 0 then
                                    match p.Bonus.Rutted with
                                    | 0 ->
                                        if p.Moves.Acceleration then
                                            str "You started the turn with at least 4 fences, you get an extra move. "
                                        else
                                            str "You have 3 moves this turn. "
                                    | 1 ->
                                        str "You're victime of a rut, you lost 2 moves. "
                                    | n ->
                                        str (sprintf "You're victime of %d ruts, you lost %d moves. " n (n*2))

                                     
                                str "Select a crossroad around you to move. "
                                if not (Hand.isEmpty p.Hand) then
                                    str "You can also play a card."
                            else
                                str "Play a card, or click on your character to end your turn."
                        | Ko _ ->
                            str (sprintf "You're eliminated. Take your revenge in the next game !")
                    else
                        str "Wait for your turn"
                
                ]
        ]



let playedCard dispatch card =
    match card with
    | Some c ->
      div [ClassName "played-card"
           OnAnimationEnd (fun _ -> dispatch HidePlayedCard) ]
          [ div [ClassName ("card " + Client.cardName c)] [] ]
    | None -> null

