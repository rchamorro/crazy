﻿namespace Elmish.Bridge

open Elmish
open Newtonsoft.Json
open Fable.Remoting.Json

module internal Helpers =
    open FSharp.Reflection
    let rec unroll (t:System.Type) =
        seq {
            if FSharpType.IsUnion t then
                yield!
                    FSharpType.GetUnionCases t
                    |> Seq.collect (fun x ->
                        match x.GetFields() with
                        |[||] -> Seq.empty
                        |[|t1|] ->
                            seq {
                                yield t1.PropertyType.FullName, t1.PropertyType, fun j -> FSharpValue.MakeUnion(x,[|j|])
                                yield! unroll t1.PropertyType |> Seq.map (fun (a,t,b) -> a, t, fun j -> FSharpValue.MakeUnion(x, [|b j|]))
                                }
                        |tuple ->
                            let t = tuple |> Array.map (fun t -> t.PropertyType) |> FSharpType.MakeTupleType
                            Seq.singleton (t.FullName.Replace('+','.'), t, fun j -> FSharpValue.MakeUnion(x, j |> FSharpValue.GetTupleFields)))
        }

type internal ServerHubData<'model, 'server, 'client> =
    { Model : 'model
      ServerDispatch : Dispatch<'server>
      ClientDispatch : Dispatch<'client> }

/// Holds functions that will be used when interaction with the `ServerHub`
type ServerHubInstance<'model, 'server, 'client> =
    { Update : 'model -> unit
      Add : 'model -> Dispatch<'server> -> Dispatch<'client> -> unit
      Remove : unit -> unit }
    static member internal Empty : ServerHubInstance<'model, 'server, 'client> =
        { Add = fun _ _ _ -> ()
          Remove = ignore
          Update = ignore }

type internal ServerHubMessages<'model, 'server, 'client> =
    | ServerBroadcast of 'server
    | ClientBroadcast of 'client
    | ServerSendIf of ('model -> bool) * 'server
    | ClientSendIf of ('model -> bool) * 'client
    | GetModels of AsyncReplyChannel<'model list>
    | AddClient of System.Guid * ServerHubData<'model, 'server, 'client>
    | UpdateModel of System.Guid * 'model
    | DropClient of System.Guid

/// Holds the data of all connected clients
type ServerHub<'model, 'server, 'client>() =
    let mutable clientMappings =
        let t = typeof<'client>
        if Reflection.FSharpType.IsUnion t then
            Helpers.unroll t
            |> Seq.map (fun (name, _, f) -> name, (fun (o : obj) -> f o :?> 'client))
            |> Map.ofSeq
        else
            Map.empty
        |> Map.add t.FullName (fun (o : obj) -> o :?> 'client)
    let mutable serverMappings =
        let t = typeof<'server>
        if Reflection.FSharpType.IsUnion t then
            Helpers.unroll t
            |> Seq.map (fun (name, _, f) -> name, (fun (o : obj) -> f o :?> 'server))
            |> Map.ofSeq
        else
            Map.empty
        |> Map.add t.FullName (fun (o : obj) -> o :?> 'server)

    let mb =
        MailboxProcessor.Start(fun inbox ->
            let rec hub data =
                async {
                    let! action = inbox.Receive()
                    match action with
                    | ServerBroadcast msg ->
                        async {
                            data
                            |> Map.toArray
                            |> Array.Parallel.iter
                                   (fun (_, { ServerDispatch = d }) -> msg |> d)
                        }
                        |> Async.Start
                    | ClientBroadcast msg ->
                        async {
                            data
                            |> Map.toArray
                            |> Array.Parallel.iter
                                   (fun (_, { ClientDispatch = d }) -> msg |> d)
                        }
                        |> Async.Start
                    | ServerSendIf(predicate, msg) ->
                        async {
                            data
                            |> Map.toArray
                            |> Array.Parallel.iter (fun (_, { Model = m;
                                                              ServerDispatch = d }) ->
                                   if predicate m then msg |> d)
                        }
                        |> Async.Start
                    | ClientSendIf(predicate, msg) ->
                        do! async {
                            data
                            |> Map.toArray
                            |> Array.Parallel.iter (fun (_, { Model = m;
                                                              ClientDispatch = d }) ->
                                   if predicate m then msg |> d)
                        }
                    | GetModels ar ->
                        async {
                            data
                            |> Map.toList
                            |> List.map (fun (_, { Model = m }) -> m)
                            |> ar.Reply
                        }
                        |> Async.Start
                    | AddClient(guid, hd) ->
                        return! hub (data |> Map.add guid hd)
                    | UpdateModel(guid, model) ->
                        return! data
                                |> Map.tryFind guid
                                |> Option.map
                                       (fun hd ->
                                       data
                                       |> Map.add guid { hd with Model = model })
                                |> Option.defaultValue data
                                |> hub
                    | DropClient(guid) -> return! hub (data |> Map.remove guid)
                    return! hub data
                }
            hub Map.empty)

    /// Register the client mappings so inner messages can be transformed to the top-level `update` message
    member this.RegisterClient(map : 'Inner -> 'client) =
        clientMappings <- clientMappings
                          |> Map.add typeof<'Inner>.FullName
                                 (fun (o : obj) -> o :?> 'Inner |> map)
        this


    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    member this.RegisterServer(map : 'Inner -> 'server) =
        serverMappings <- serverMappings
                          |> Map.add typeof<'Inner>.FullName
                                 (fun (o : obj) -> o :?> 'Inner |> map)
        this

    abstract BroadcastClient : 'inner -> unit
    abstract BroadcastServer : 'inner -> unit
    abstract SendClientIf : ('model -> bool) -> 'inner -> unit
    abstract SendServerIf : ('model -> bool) -> 'inner -> unit
    abstract GetModels : unit -> 'model list

    /// Send client message for all connected users
    default __.BroadcastClient(msg : 'inner) =
        clientMappings
        |> Map.tryFind typeof<'inner>.FullName
        |> Option.iter (fun f ->
               f msg
               |> ClientBroadcast
               |> mb.Post)

    /// Send server message for all connected users
    default __.BroadcastServer(msg : 'inner) =
        serverMappings
        |> Map.tryFind typeof<'inner>.FullName
        |> Option.iter (fun f ->
               f msg
               |> ServerBroadcast
               |> mb.Post)

    /// Send client message for all connected users if their `model` passes the predicate
    default __.SendClientIf predicate (msg : 'inner) =
        clientMappings
        |> Map.tryFind typeof<'inner>.FullName
        |> Option.iter (fun f ->
               (predicate, f msg)
               |> ClientSendIf
               |> mb.Post)

    /// Send server message for all connected users if their `model` passes the predicate
    default __.SendServerIf predicate (msg : 'inner) =
        serverMappings
        |> Map.tryFind typeof<'inner>.FullName
        |> Option.iter (fun f ->
               (predicate, f msg)
               |> ServerSendIf
               |> mb.Post)

    /// Return the model of all connected users
    default __.GetModels() = mb.PostAndReply GetModels

    member private __.Init() : ServerHubInstance<'model, 'server, 'client> =
        let guid = System.Guid.NewGuid()

        let add =
            fun model serverDispatch clientDispatch ->
                mb.Post(AddClient(guid,
                                  { Model = model
                                    ServerDispatch = serverDispatch
                                    ClientDispatch = clientDispatch }))

        let remove = fun () -> mb.Post(DropClient guid)
        let update = fun model -> mb.Post(UpdateModel(guid, model))
        { Add = add
          Remove = remove
          Update = update }

    /// Used to create a default `ServerHubInstance` that does nothing when the `ServerHub` is not set
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    static member Initialize(sh : ServerHub<'model, 'server, 'client> option) =
        sh
        |> Option.map (fun sh -> sh.Init())
        |> Option.defaultValue ServerHubInstance.Empty

type BridgeDeserializer<'server> =
    | Text of (string -> 'server)
    | Binary of (byte[] -> 'server)



/// Defines server configuration
type BridgeServer<'arg, 'model, 'server, 'client, 'impl>(endpoint : string, init, update: Dispatch<'client> -> 'server -> 'model ->  ('model * Cmd<'server>) Async) =
    let mutable subscribe = fun _ -> Cmd.none
    let mutable logMsg = ignore
    let mutable logRegister = ignore
    let mutable logSMsg = ignore
    let mutable logInit = ignore
    let mutable logModel = ignore
    static let c = [|FableJsonConverter() :> JsonConverter|]

    static let s =
        let js = Newtonsoft.Json.JsonSerializer()
        js.Converters.Add (FableJsonConverter())
        js

    let mutable mappings =
        let t = typeof<'server>
        if Reflection.FSharpType.IsUnion t then
            Helpers.unroll t
                |> Seq.map (fun (n,t,f) -> n.Replace('+','.'),t,f)
                |> Seq.groupBy (fun (a,_,_)->a)
                |> Seq.collect
                    (fun (_, f) ->
                        match f |> Seq.tryItem 1 with
                        |None -> f |> Seq.map (fun (a,t,f) -> a, (t,f))
                        |Some _ -> Seq.empty)

                |> Map.ofSeq
                |> Map.add (t.FullName.Replace('+','.')) (t,id)
                |> Map.map (fun _ (t,f) ->
                    Text(
                      fun (i : string) ->
                        Newtonsoft.Json.JsonConvert.DeserializeObject(i,t,c)
                        |> f :?> 'server))

        else
            Map.empty


    let write (o : 'client) = Newtonsoft.Json.JsonConvert.SerializeObject(o, c)
    let read dispatch str =
        logSMsg str
        let (name : string, o : string) =
            Newtonsoft.Json.JsonConvert.DeserializeObject
                (str, typeof<string * string>, c) :?> _
        mappings
        |> Map.tryFind name
        |> Option.iter (function
            | Text e -> e o
            | Binary e -> e (System.Convert.FromBase64String o)
            >> dispatch)


    let mutable whenDown : 'server option = None
    let mutable serverHub : ServerHub<'model, 'server, 'client> option = None

    /// Server msg passed to the `update` function when the connection is closed
    member this.WithWhenDown n =
        whenDown <- Some n
        this

    /// Registers the `ServerHub` that will be used by this socket connections
    member this.WithServerHub sh =
        serverHub <- Some sh
        this

    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    member this.Register<'Inner, 'server>(map : 'Inner -> 'server) =
        let t = typeof<'Inner>
        let name = t.FullName.Replace('+','.')
        logRegister name
        mappings <- mappings
                    |> Map.add name
                           (Text
                             (fun (i : string) ->
                              Newtonsoft.Json.JsonConvert.DeserializeObject<'Inner>(i,c) |> map))
        this
    /// Register the server mappings so inner messages can be transformed to the top-level `update` message using a custom deserializer
    member this.RegisterWithDeserializer<'Inner, 'server>(map : 'Inner -> 'server, deserializer ) =
        let t = typeof<'Inner>
        let name = t.FullName.Replace('+','.')
        logRegister name
        mappings <- mappings
                    |> Map.add name
                        (match deserializer with Text e -> Text (e >> map) | Binary e -> Binary (e >> map))
        this
    /// Subscribe to external source of events.
    /// The subscription is called once - with the initial model, but can dispatch new messages at any time.
    member this.WithSubscription sub =
        let oldSubscribe = subscribe
        let sub model =
            Cmd.batch [ oldSubscribe model
                        sub model ]
        subscribe <- sub
        this

    /// Add a log function for the initial model
    member this.AddInitLogging log =
        let oldLogInit = logInit
        logInit <- fun m ->
            oldLogInit m
            log m
        this

    /// Add a log function for logging type names on registering
    member this.AddRegisterLogging log =
        let oldLogRegister = logRegister
        logRegister <- fun m ->
            oldLogRegister m
            log m
        this

    /// Add a log function after the model updating
    member this.AddModelLogging log =
        let oldLogModel = logModel
        logModel <- fun m ->
            oldLogModel m
            log m
        this

    /// Add a log function when receiving a new message
    member this.AddMsgLogging log =
        let oldLogMsg = logMsg
        logMsg <- fun m ->
            oldLogMsg m
            log m
        this

    /// Add a log function when receiving a raw socket message
    member this.AddSocketRawMsgLogging log =
        let oldLogSMsg = logSMsg
        logSMsg <- fun m ->
            oldLogSMsg m
            log m
        this

    /// Trace all the operation to the console
    member this.WithConsoleTracing =
        this.AddInitLogging(eprintfn "Initial state: %A")
            .AddMsgLogging(eprintfn "New message: %A")
            .AddSocketRawMsgLogging(eprintfn "Remote message: %s")
            .AddRegisterLogging(eprintfn "Type %s registered")
            .AddModelLogging(eprintfn "Updated state: %A")
    /// Internal use only
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    member this.Start server (arg : 'arg) : 'impl =
        let inbox action =
            let mb =
              MailboxProcessor.Start(fun (mb : MailboxProcessor<'server option>) ->
                let clientDispatch =
                    write
                    >> action
                    >> Async.Start
                let hubInstance = ServerHub.Initialize serverHub
                let model, msgs = init clientDispatch arg
                logInit model
                let sub =
                    try
                        hubInstance.Add model (Some >> mb.Post) clientDispatch
                        subscribe model
                    with _ -> Cmd.none
                sub @ msgs |> List.iter (fun sub -> sub (Some >> mb.Post))
                let rec loop (state : 'model) =
                    async {
                        let! msg = mb.Receive()
                        match msg with
                        | Some msg ->
                            logMsg msg
                            let! model, msgs = update clientDispatch msg state
                            logModel model
                            msgs
                            |> List.iter
                                   (fun sub -> sub (Some >> mb.Post))
                            hubInstance.Update model
                            return! loop model
                        | None ->
                            match whenDown with
                            | Some msg ->
                                    logMsg msg
                                    let! model, _ = update clientDispatch msg state
                                    logModel model
                            | None -> ()
                            hubInstance.Remove()
                            return ()
                    }
                loop model)
            (Some >> mb.Post |> read),(fun () -> mb.Post None)
        server endpoint inbox

[<RequireQualifiedAccess>]
module Bridge =
    /// Creates a `ServerBridge`
    /// Takes an `endpoint` where the server will listen for connections
    /// a `init` : `Dispatch<'client> -> 'arg -> 'model * Cmd<'server>`
    /// and a `update` : `Dispatch<'client> -> 'server -> 'model -> 'model * Cmd<'server>`
    /// Typical program, new commands are produced by `init` and `update` along with the new state.
    let mkServer endpoint
        (init : Dispatch<'client> -> 'arg -> ('model * Cmd<'server>))
        (update : Dispatch<'client> -> 'server -> 'model ->  Async<'model * Cmd<'server>>) =
        BridgeServer(endpoint, init, update)

    /// Subscribe to external source of events.
    /// The subscription is called once - with the initial model, but can dispatch new messages at any time.
    let withSubscription subscribe (program : BridgeServer<_, _, _, _, _>) =
        program.WithSubscription subscribe

    /// Log changes on the model and received messages to the console
    let withConsoleTrace (program : BridgeServer<_, _, _, _, _>) =
        program.WithConsoleTracing

    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    let register map (program : BridgeServer<_, _, _, _, _>) =
        program.Register map

    /// Register the server mappings so inner messages can be transformed to the top-level `update` message
    let registerWithDeserializer map des (program : BridgeServer<_, _, _, _, _>) =
        program.RegisterWithDeserializer(map, des)

    /// Registers the `ServerHub` that will be used by this socket connections
    let withServerHub hub (program : BridgeServer<_, _, _, _, _>) =
        program.WithServerHub hub

    /// Server msg passed to the `update` function when the connection is closed
    let whenDown msg (program : BridgeServer<_, _, _, _, _>) =
        program.WithWhenDown msg

    /// Creates a websocket loop.
    /// `arg`: argument to pass to the `init` function.
    /// `program`: program created with `mkProgram`.
    let runWith server arg (program : BridgeServer<_, _, _, _, _>) =
        program.Start server arg

    /// Creates a websocket loop with `unit` for the `init` function.
    /// `program`: program created with `mkProgram`.
    let run server (program : BridgeServer<_, _, _, _, _>) =
        program.Start server ()

[<RequireQualifiedAccess>]
module Giraffe =
    open System
    open Giraffe
    open FSharp.Control.Tasks.ContextInsensitive
    open System.Net.WebSockets
    open Microsoft.AspNetCore.Builder
    open Microsoft.AspNetCore.Http
    open System.Threading

    /// Giraffe's server used by `ServerProgram.runServerAtWith` and `ServerProgram.runServerAt`
    /// Creates a `HttpHandler`
    let server endpoint inboxCreator : HttpHandler =
        let ws (next : HttpFunc) (ctx : HttpContext) =
            task {
                if ctx.WebSockets.IsWebSocketRequest then
                    let! webSocket = ctx.WebSockets.AcceptWebSocketAsync()
                    let (sender,closer) =
                        inboxCreator
                            (fun (s:string) ->
                            let resp =
                                s
                                |> System.Text.Encoding.UTF8.GetBytes
                                |> ArraySegment
                            webSocket.SendAsync
                                (resp, WebSocketMessageType.Text, true,
                                 CancellationToken.None) |> Async.AwaitTask)
                    let skt =
                        task {
                            let buffer = Array.zeroCreate 4096
                            let mutable loop = true
                            let mutable frame = []
                            while loop do
                                let! msg = webSocket.ReceiveAsync
                                               (ArraySegment(buffer),
                                                CancellationToken.None)
                                match msg.MessageType,
                                      buffer.[0..msg.Count - 1],
                                      msg.EndOfMessage, msg.CloseStatus with
                                | _, _, _, s when s.HasValue ->
                                    do! webSocket.CloseOutputAsync
                                            (WebSocketCloseStatus.NormalClosure,
                                             null, CancellationToken.None)
                                    loop <- false
                                | WebSocketMessageType.Text, data, complete,
                                  _ ->
                                    frame <- data :: frame
                                    if complete then
                                        frame
                                        |> List.rev
                                        |> Array.concat
                                        |> System.Text.Encoding.UTF8.GetString
                                        |> sender
                                        frame <- []
                                | _ -> ()
                        }
                    try
                        do! skt
                    with _ -> ()
                    closer()
                    return Some ctx
                else return None
            }
        route endpoint >=> ws

    /// Prepare app to use websockets
    let useWebSockets (app : IApplicationBuilder) = app.UseWebSockets(WebSocketOptions(KeepAliveInterval=TimeSpan.FromSeconds 30.))