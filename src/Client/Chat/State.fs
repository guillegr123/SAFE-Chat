module Chat.State

open Elmish
open Elmish.Browser.Navigation

open Fable.Import.Browser
open Fable.Websockets.Elmish
open Fable.Websockets.Protocol
open Fable.Websockets.Elmish.Types

open Router

open Channel.Types
open Chat.Types

open FsChat

module private Conversions =

    let mapUserInfo isMe (u: Protocol.ChanUserInfo) :UserInfo =
        { Id = u.id; Nick = u.nick; IsBot = u.isbot
          Status = u.status
          Online = true; ImageUrl = Core.Option.ofObj u.imageUrl
          isMe = isMe u.id}

    let mapChannel (ch: Protocol.ChannelInfo) : ChannelInfo =
        {Id = ch.id; Name = ch.name; Topic = ch.topic; UserCount = ch.userCount}

module private Implementation =

    let updateChanCmd chanId (f: ChannelData -> ChannelData * Channel.Types.Msg Cmd) (chat: ChatData) : ChatData * Chat.Types.AppMsg Cmd=
        match chat.Channels |> Map.tryFind chanId with
        | Some channel ->
            match f channel with
            | newData, cmd when newData = channel && cmd.IsEmpty -> chat, Cmd.none
            | newData, cmd ->
                { chat with Channels = chat.Channels |> Map.add chanId newData },
                  cmd |> Cmd.map (fun x -> Types.ChannelMsg (chanId, x))
        | None ->
            console.error ("Channel %s update failed - channel not found", chanId)
            chat, Cmd.none

    let updateChan chanId (f: ChannelData -> ChannelData) (chat: ChatData) : ChatData =
        match chat.Channels |> Map.tryFind chanId with
        | Some channel ->
            match f channel with
            | newData when newData = channel -> chat
            | newData -> { chat with Channels = chat.Channels |> Map.add chanId newData }
        | None ->
            console.error ("Channel %s update failed - channel not found", chanId)
            chat

    let applicationMsgUpdate (msg: AppMsg) (state: ChatData) :(ChatData * Msg Cmd) =

        match msg with
        | Nop -> state, Cmd.none

        | ChannelMsg (chanId, Forward text) ->
            let message : string -> _ = function
                | text when text.StartsWith "/" -> Protocol.ControlCommand | _ -> Protocol.UserMessage

            state, Cmd.ofSocketMessage state.socket (message text {text = text; chan = chanId})

        | ChannelMsg (chanId, Msg.Leave) ->
            state, Cmd.ofSocketMessage state.socket (Protocol.ServerMsg.Leave chanId)

        | ChannelMsg (chanId, msg) ->
            let newState, cmd = state |> updateChanCmd chanId (Channel.State.update msg)
            newState, Cmd.map ApplicationMsg cmd

        | SetNewChanName name ->
            { state with NewChanName = name }, Cmd.none
            
        | CreateJoin ->
            match state.NewChanName with
            | Some channelName ->
                state, Cmd.batch
                        [ Cmd.ofSocketMessage state.socket (Protocol.JoinOrCreate channelName)
                          Cmd.ofMsg <| SetNewChanName None |> Cmd.map ApplicationMsg]
            | None -> state, Cmd.none
        | Join chanId ->
            state, Cmd.ofSocketMessage state.socket (Protocol.Join chanId)
        | Leave chanId ->
            state, Cmd.ofSocketMessage state.socket (Protocol.Leave chanId)

    let unknownUser userId = {
        Id = userId; Nick = "Unknown #" + userId; Status = ""
        IsBot = false; Online = true; ImageUrl = None; isMe = false}

    let getUser (userId: string) (users: Map<UserId,UserInfo>) : UserInfo =
        users |> Map.tryFind userId |> Core.Option.defaultWith (fun () -> unknownUser userId)
        
    let chatUpdate isMe (msg: Protocol.ClientMsg) (state: ChatData) : ChatData * Cmd<Msg> =
        match msg with
        | Protocol.ClientMsg.ChanMsg msg ->
            let message = AppendUserMessage (msg.author, {Id = msg.id; Ts = msg.ts; Content = msg.text})
            let newState, cmd = state |> updateChanCmd msg.chan (Channel.State.update message)
            newState, cmd |> Cmd.map ApplicationMsg

        | Protocol.ClientMsg.UserEvent ev ->

            let user = Conversions.mapUserInfo isMe ev.user

            let chan, message =
                ev.evt |> function
                | Protocol.Joined chan -> chan, Channel.Types.UserJoined user
                | Protocol.Left chan -> chan, Channel.Types.UserLeft ev.user.id
                | Protocol.Updated chan -> chan, Channel.Types.UserUpdated user

            updateChanCmd chan (Channel.State.update message) state |> fst, Cmd.none

        | Protocol.ClientMsg.NewChannel chan ->
            { state with ChannelList = state.ChannelList |> Map.add chan.id (Conversions.mapChannel chan)}, Cmd.none

        | Protocol.ClientMsg.RemoveChannel chan ->
            { state with ChannelList = state.ChannelList |> Map.remove chan.id }, Cmd.none

        | notProcessed ->
            printfn "message was not processed: %A" notProcessed
            state, Cmd.none

    let socketMsgUpdate msg =
        function
        | Connected (me, chat) as state ->
            let isMe = (=) me.Id
            match msg with

            | Protocol.ClientMsg.Hello hello ->
                let me = Conversions.mapUserInfo ((=) hello.me.id) hello.me
                let mapChannel (ch: Protocol.ChannelInfo) = ch.id, Conversions.mapChannel ch
                let chatData = {
                    ChatData.Empty with
                      socket = chat.socket
                      ChannelList = hello.channels |> List.map mapChannel |> Map.ofList }
                Connected (me, chatData), Cmd.none

            | Protocol.ClientMsg.UserUpdated newUser ->
                let meNew = Conversions.mapUserInfo isMe newUser
                Connected (meNew, chat), Cmd.none

            | Protocol.ClientMsg.JoinedChannel chanInfo ->
                let channel = Conversions.mapChannel chanInfo
                let users = chanInfo.users |> List.map (Conversions.mapUserInfo isMe)
                let (chanData, cmd) =
                    Channel.State.init() |> fst
                    |> Channel.State.update (Init (channel, users))
                    // Conversions.mapChannel isMe chanInfo
                Connected (me, {chat with Channels = chat.Channels |> Map.add chanInfo.id chanData}),
                    Cmd.batch [
                        cmd |> Cmd.map (fun msg -> ChannelMsg (chanInfo.id, msg) |> ApplicationMsg)
                        Channel chanInfo.id |> toHash |> Navigation.newUrl
                    ]

            | Protocol.ClientMsg.LeftChannel channelId ->
                chat.Channels |> Map.tryFind channelId
                |> function
                | Some _ ->
                    Connected (me, {chat with Channels = chat.Channels |> Map.remove channelId}),
                        About |> toHash |> Navigation.newUrl
                | _ ->
                    printfn "Channel not found %s" channelId
                    state, Cmd.none

            | protocolMsg ->
                let chatData, cmd = chatUpdate isMe protocolMsg chat
                Connected (me, chatData), cmd
        | other ->
            printfn "Socket message %A" other
            (other, Cmd.none)

open Implementation

let init () : ChatState * Cmd<Msg> =
    let socketAddr = sprintf "ws://%s/api/socket" location.host
    console.debug ("Opening socket at '%s'", socketAddr)
    NotConnected, Cmd.tryOpenSocket socketAddr

let update msg state : ChatState * Cmd<Msg> = 
    match msg with
    | ApplicationMsg amsg ->
        match state with
        | Connected (me, chat) ->
            let newChat, cmd = applicationMsgUpdate amsg chat
            Connected(me, newChat), cmd
        | _ ->
            console.error <| "Failed to process channel message. Server is not connected"
            state, Cmd.none
    | WebsocketMsg (socket, Opened) ->
        Connected (UserInfo.Anon, { ChatData.Empty with socket = socket }), Cmd.ofSocketMessage socket Protocol.ServerMsg.Greets
    | WebsocketMsg (_, Msg socketMsg) ->
        socketMsgUpdate socketMsg state
    | _ -> (state, Cmd.none)
