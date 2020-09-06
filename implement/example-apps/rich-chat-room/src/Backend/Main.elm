module Backend.Main exposing
    ( State
    , interfaceToHost_initState
    , interfaceToHost_processEvent
    , processEvent
    )

import Backend.InterfaceToHost as InterfaceToHost
import Base64
import Bytes
import Bytes.Decode
import Bytes.Encode
import Conversation exposing (UserId)
import Dict
import ElmFullstackCompilerInterface.ElmMake
import ElmFullstackCompilerInterface.GenerateJsonCoders as GenerateJsonCoders
import ElmFullstackCompilerInterface.SourceFiles
import FrontendBackendInterface
import Json.Decode
import Json.Encode
import SHA1
import Url


type alias State =
    { posixTimeMilli : Int
    , conversationHistory : List Conversation.Event
    , usersSessions : Dict.Dict String UserSessionState
    , usersProfiles : Dict.Dict UserId UserProfile
    , usersLastSeen : Dict.Dict UserId { posixTime : Int }
    , pendingHttpRequests : Dict.Dict String PendingHttpRequest
    }


type alias PendingHttpRequest =
    { posixTimeMilli : Int
    , userSessionId : String
    , userId : Maybe UserId
    , requestFromUser : FrontendBackendInterface.RequestFromUser
    }


type alias UserSessionState =
    { beginPosixTime : Int
    , clientAddress : Maybe String
    , userId : Maybe UserId
    , lastUsePosixTime : Int
    }


type alias UserProfile =
    { chosenName : String
    }


interfaceToHost_processEvent : String -> State -> ( State, String )
interfaceToHost_processEvent =
    InterfaceToHost.wrapForSerialInterface_processEvent processEvent


processEvent : InterfaceToHost.AppEvent -> State -> ( State, InterfaceToHost.AppEventResponse )
processEvent hostEvent stateBefore =
    let
        ( stateBeforeHttpResponses, responseBeforeHttpResponses ) =
            processEventWithoutHttpResponses hostEvent stateBefore

        ( state, httpResponses ) =
            processPendingHttpRequests stateBeforeHttpResponses

        notifyWhenArrivedAtTime =
            if Dict.isEmpty state.pendingHttpRequests then
                Nothing

            else
                Just { posixTimeMilli = state.posixTimeMilli + 1000 }

        response =
            { responseBeforeHttpResponses | notifyWhenArrivedAtTime = notifyWhenArrivedAtTime }
                |> InterfaceToHost.withCompleteHttpResponsesAdded httpResponses
    in
    ( state
    , response
    )


processPendingHttpRequests : State -> ( State, List InterfaceToHost.HttpResponseRequest )
processPendingHttpRequests stateBefore =
    let
        ( state, httpResponses ) =
            stateBefore.pendingHttpRequests
                |> Dict.toList
                |> List.foldl
                    (\( pendingHttpRequestId, pendingHttpRequest ) ( intermediateState, intermediateHttpResponses ) ->
                        case
                            processRequestFromUser
                                { posixTimeMilli = stateBefore.posixTimeMilli
                                , loginUrl = ""
                                }
                                { posixTimeMilli = pendingHttpRequest.posixTimeMilli
                                , userId = pendingHttpRequest.userId
                                , requestFromUser = pendingHttpRequest.requestFromUser
                                }
                                intermediateState
                        of
                            Nothing ->
                                ( intermediateState
                                , intermediateHttpResponses
                                )

                            Just ( completeHttpResponseState, completeHttpResponseResponseToUser ) ->
                                let
                                    responseToClient =
                                        { currentPosixTimeMilli = intermediateState.posixTimeMilli
                                        , currentUserId = pendingHttpRequest.userId
                                        , responseToUser =
                                            completeHttpResponseResponseToUser
                                        }

                                    httpResponse =
                                        { httpRequestId = pendingHttpRequestId
                                        , response =
                                            { statusCode = 200
                                            , bodyAsBase64 =
                                                responseToClient
                                                    |> GenerateJsonCoders.jsonEncodeMessageToClient
                                                    |> Json.Encode.encode 0
                                                    |> encodeStringToBytes
                                                    |> Base64.fromBytes
                                            , headersToAdd = []
                                            }
                                                |> addCookieUserSessionId pendingHttpRequest.userSessionId
                                        }
                                in
                                ( completeHttpResponseState
                                , httpResponse :: intermediateHttpResponses
                                )
                    )
                    ( stateBefore, [] )

        pendingHttpRequests =
            httpResponses
                |> List.map .httpRequestId
                |> List.foldl Dict.remove stateBefore.pendingHttpRequests
    in
    ( { state | pendingHttpRequests = pendingHttpRequests }
    , httpResponses
    )


processEventWithoutHttpResponses : InterfaceToHost.AppEvent -> State -> ( State, InterfaceToHost.AppEventResponse )
processEventWithoutHttpResponses hostEvent stateBefore =
    case hostEvent of
        InterfaceToHost.HttpRequestEvent httpRequestEvent ->
            processEventHttpRequest httpRequestEvent { stateBefore | posixTimeMilli = httpRequestEvent.posixTimeMilli }

        InterfaceToHost.TaskCompleteEvent _ ->
            ( stateBefore, InterfaceToHost.passiveAppEventResponse )

        InterfaceToHost.ArrivedAtTimeEvent _ ->
            ( stateBefore, InterfaceToHost.passiveAppEventResponse )


processEventHttpRequest : InterfaceToHost.HttpRequestEventStructure -> State -> ( State, InterfaceToHost.AppEventResponse )
processEventHttpRequest httpRequestEvent stateBefore =
    let
        respondWithFrontendHtmlDocument { enableInspector } =
            ( stateBefore
            , InterfaceToHost.passiveAppEventResponse
                |> InterfaceToHost.withCompleteHttpResponsesAdded
                    [ { httpRequestId = httpRequestEvent.httpRequestId
                      , response =
                            { statusCode = 200
                            , bodyAsBase64 =
                                Just
                                    (if enableInspector then
                                        ElmFullstackCompilerInterface.ElmMake.elm_make__debug__base64____src_FrontendWeb_Main_elm

                                     else
                                        ElmFullstackCompilerInterface.ElmMake.elm_make__base64____src_FrontendWeb_Main_elm
                                    )
                            , headersToAdd = []
                            }
                      }
                    ]
            )
    in
    case
        httpRequestEvent.request.uri
            |> Url.fromString
            |> Maybe.andThen FrontendBackendInterface.routeFromUrl
    of
        Nothing ->
            respondWithFrontendHtmlDocument { enableInspector = False }

        Just FrontendBackendInterface.FrontendWithInspectorRoute ->
            respondWithFrontendHtmlDocument { enableInspector = True }

        Just FrontendBackendInterface.ApiRoute ->
            case
                httpRequestEvent.request.bodyAsBase64
                    |> Maybe.map (Base64.toBytes >> Maybe.map (decodeBytesToString >> Maybe.withDefault "Failed to decode bytes to string") >> Maybe.withDefault "Failed to decode from base64")
                    |> Maybe.withDefault "Missing HTTP body"
                    |> Json.Decode.decodeString GenerateJsonCoders.jsonDecodeRequestFromUser
            of
                Err decodeError ->
                    let
                        httpResponse =
                            { httpRequestId = httpRequestEvent.httpRequestId
                            , response =
                                { statusCode = 400
                                , bodyAsBase64 =
                                    ("Failed to decode request: " ++ (decodeError |> Json.Decode.errorToString))
                                        |> encodeStringToBytes
                                        |> Base64.fromBytes
                                , headersToAdd = []
                                }
                            }
                    in
                    ( stateBefore
                    , InterfaceToHost.passiveAppEventResponse
                        |> InterfaceToHost.withCompleteHttpResponsesAdded [ httpResponse ]
                    )

                Ok requestFromUser ->
                    let
                        ( userSessionId, userSessionStateBefore ) =
                            userSessionIdAndStateFromRequestOrCreateNew httpRequestEvent.request stateBefore

                        userSessionState =
                            userSessionStateBefore

                        usersLastSeen =
                            case userSessionStateBefore.userId of
                                Nothing ->
                                    stateBefore.usersLastSeen

                                Just userId ->
                                    stateBefore.usersLastSeen
                                        |> Dict.insert userId { posixTime = stateBefore.posixTimeMilli // 1000 }

                        usersSessions =
                            stateBefore.usersSessions
                                |> Dict.insert userSessionId userSessionState
                    in
                    ( { stateBefore
                        | usersLastSeen = usersLastSeen
                        , usersSessions = usersSessions
                        , pendingHttpRequests =
                            stateBefore.pendingHttpRequests
                                |> Dict.insert httpRequestEvent.httpRequestId
                                    { posixTimeMilli = httpRequestEvent.posixTimeMilli
                                    , userSessionId = userSessionId
                                    , userId = userSessionStateBefore.userId
                                    , requestFromUser = requestFromUser
                                    }
                      }
                    , InterfaceToHost.passiveAppEventResponse
                    )

        Just (FrontendBackendInterface.StaticContentRoute contentName) ->
            let
                httpResponse =
                    case availableStaticContent |> Dict.get contentName of
                        Nothing ->
                            { statusCode = 404
                            , bodyAsBase64 =
                                ("Found no content with the name " ++ contentName)
                                    |> encodeStringToBytes
                                    |> Base64.fromBytes
                            , headersToAdd = []
                            }

                        Just content ->
                            { statusCode = 200
                            , bodyAsBase64 = content |> Base64.fromBytes
                            , headersToAdd = [ { name = "Cache-Control", values = [ "public, max-age=31536000" ] } ]
                            }
            in
            ( stateBefore
            , InterfaceToHost.passiveAppEventResponse
                |> InterfaceToHost.withCompleteHttpResponsesAdded
                    [ { httpRequestId = httpRequestEvent.httpRequestId
                      , response = httpResponse
                      }
                    ]
            )


availableStaticContent : Dict.Dict String Bytes.Bytes
availableStaticContent =
    [ ElmFullstackCompilerInterface.SourceFiles.file____static_chat_message_added_0_mp3 ]
        |> List.map (\content -> ( content |> FrontendBackendInterface.staticContentFileName, content ))
        |> Dict.fromList


seeingLobbyFromState : State -> FrontendBackendInterface.SeeingLobbyStructure
seeingLobbyFromState state =
    let
        usersOnline =
            state.usersLastSeen
                |> Dict.filter (\_ lastSeen -> state.posixTimeMilli // 1000 - 10 < lastSeen.posixTime)
                |> Dict.keys
    in
    { conversationHistory = state.conversationHistory
    , usersOnline = usersOnline
    }


processRequestFromUser :
    { posixTimeMilli : Int, loginUrl : String }
    -> { posixTimeMilli : Int, userId : Maybe Int, requestFromUser : FrontendBackendInterface.RequestFromUser }
    -> State
    -> Maybe ( State, FrontendBackendInterface.ResponseToUser )
processRequestFromUser context requestFromUser stateBefore =
    case requestFromUser.requestFromUser of
        FrontendBackendInterface.ShowUpRequest showUpRequest ->
            let
                seeingLobby =
                    seeingLobbyFromState stateBefore

                requestAgeMilli =
                    context.posixTimeMilli - requestFromUser.posixTimeMilli
            in
            if
                ((seeingLobby.conversationHistory |> List.map .posixTimeMilli |> List.maximum)
                    /= showUpRequest.lastSeenEventPosixTimeMilli
                )
                    || (5000 <= requestAgeMilli)
            then
                Just
                    ( stateBefore
                    , FrontendBackendInterface.SeeingLobby seeingLobby
                    )

            else
                Nothing

        FrontendBackendInterface.AddTextMessageRequest message ->
            case requestFromUser.userId of
                Nothing ->
                    Just
                        ( stateBefore
                        , ([ Conversation.LeafPlainText "⚠️ To add a message, please sign in first at "
                           , Conversation.LeafLinkToUrl { url = context.loginUrl }
                           ]
                            |> Conversation.SequenceOfNodes
                          )
                            |> FrontendBackendInterface.MessageToUser
                        )

                Just userId ->
                    if hasUserExhaustedRateLimitToAddMessage stateBefore { userId = userId } then
                        Just
                            ( stateBefore
                            , Conversation.LeafPlainText "❌ Too many messages – Maximum sending rate exceeded."
                                |> FrontendBackendInterface.MessageToUser
                            )

                    else
                        let
                            conversationHistory =
                                { posixTimeMilli = stateBefore.posixTimeMilli
                                , origin = Conversation.FromUser { userId = userId }
                                , message = Conversation.LeafPlainText message
                                }
                                    :: stateBefore.conversationHistory

                            stateAfterAddingMessage =
                                { stateBefore | conversationHistory = conversationHistory }
                        in
                        Just
                            ( stateAfterAddingMessage
                            , stateAfterAddingMessage |> seeingLobbyFromState |> FrontendBackendInterface.SeeingLobby
                            )

        FrontendBackendInterface.ChooseNameRequest chosenName ->
            case requestFromUser.userId of
                Nothing ->
                    Just
                        ( stateBefore
                        , ([ Conversation.LeafPlainText "⚠️ To choose a name, please sign in first at "
                           , Conversation.LeafLinkToUrl { url = context.loginUrl }
                           ]
                            |> Conversation.SequenceOfNodes
                          )
                            |> FrontendBackendInterface.MessageToUser
                        )

                Just userId ->
                    let
                        userProfileBefore =
                            stateBefore.usersProfiles |> Dict.get userId |> Maybe.withDefault initUserProfile

                        userProfile =
                            { userProfileBefore | chosenName = chosenName }

                        usersProfiles =
                            stateBefore.usersProfiles |> Dict.insert userId userProfile
                    in
                    Just
                        ( { stateBefore | usersProfiles = usersProfiles }
                        , userProfile |> FrontendBackendInterface.ReadUserProfile
                        )

        FrontendBackendInterface.ReadUserProfileRequest userId ->
            Just
                ( stateBefore
                , stateBefore.usersProfiles
                    |> Dict.get userId
                    |> Maybe.withDefault initUserProfile
                    |> FrontendBackendInterface.ReadUserProfile
                )


initUserProfile : UserProfile
initUserProfile =
    { chosenName = "" }


userSessionIdAndStateFromRequestOrCreateNew : InterfaceToHost.HttpRequestProperties -> State -> ( String, UserSessionState )
userSessionIdAndStateFromRequestOrCreateNew httpRequest state =
    state |> getFirstMatchingUserSessionOrCreateNew (getSessionIdsFromHttpRequest httpRequest)


getFirstMatchingUserSessionOrCreateNew : List String -> State -> ( String, UserSessionState )
getFirstMatchingUserSessionOrCreateNew sessionIds state =
    sessionIds
        |> List.filterMap
            (\requestSessionId ->
                state.usersSessions
                    |> Dict.get requestSessionId
                    |> Maybe.map
                        (\sessionState ->
                            ( requestSessionId, sessionState )
                        )
            )
        |> List.sortBy
            (\( _, sessionState ) ->
                if sessionState.userId == Nothing then
                    1

                else
                    0
            )
        |> List.head
        |> Maybe.withDefault (state |> getNextUserSessionIdAndState)


getSessionIdsFromHttpRequest : InterfaceToHost.HttpRequestProperties -> List String
getSessionIdsFromHttpRequest httpRequest =
    let
        cookies =
            httpRequest.headers
                |> List.filter (\{ name } -> (name |> String.toLower) == "cookie")
                |> List.head
                |> Maybe.map .values
                |> Maybe.withDefault []
                |> List.concatMap (String.split ";")
                |> List.map String.trim

        prefix =
            httpSessionIdCookieName ++ "="
    in
    cookies
        |> List.filter (String.startsWith prefix)
        |> List.map (\sessionIdCookie -> sessionIdCookie |> String.dropLeft (prefix |> String.length))


getNextUserSessionIdAndState : State -> ( String, UserSessionState )
getNextUserSessionIdAndState state =
    let
        posixTime =
            state.posixTimeMilli // 1000
    in
    ( state |> getNextUserSessionId
    , { userId = Just ((state |> getLastUserId |> Maybe.withDefault 0) + 1)
      , beginPosixTime = posixTime
      , lastUsePosixTime = posixTime
      , clientAddress = Nothing
      }
    )


getLastUserId : State -> Maybe Int
getLastUserId state =
    [ state.usersProfiles |> Dict.keys
    , state.usersSessions |> Dict.values |> List.filterMap .userId
    ]
        |> List.concat
        |> List.maximum


getNextUserSessionId : State -> String
getNextUserSessionId state =
    let
        otherSessionsIds =
            state.usersSessions |> Dict.keys

        source =
            (otherSessionsIds |> String.concat) ++ (state.posixTimeMilli |> String.fromInt)
    in
    source |> SHA1.fromString |> SHA1.toHex |> String.left 30


hasUserExhaustedRateLimitToAddMessage : State -> { userId : Int } -> Bool
hasUserExhaustedRateLimitToAddMessage state { userId } =
    let
        addedMessagesAges =
            state.conversationHistory
                |> List.filterMap
                    (\event ->
                        case event.origin of
                            Conversation.FromSystem ->
                                Nothing

                            Conversation.FromUser fromUser ->
                                if fromUser.userId /= userId then
                                    Nothing

                                else
                                    Just event.posixTimeMilli
                    )
                |> List.map (\addedMessagePosixTimeMilli -> (state.posixTimeMilli - addedMessagePosixTimeMilli) // 1000)

        numberOfMessagesWithinAge ageInSeconds =
            addedMessagesAges
                |> List.filter (\messageAge -> messageAge <= ageInSeconds)
                |> List.length
    in
    userAddMessageRateLimits
        |> List.any (\limit -> limit.numberOfMessages <= numberOfMessagesWithinAge limit.timespanInSeconds)


userAddMessageRateLimits : List { timespanInSeconds : Int, numberOfMessages : Int }
userAddMessageRateLimits =
    [ { timespanInSeconds = 3, numberOfMessages = 2 }
    , { timespanInSeconds = 10, numberOfMessages = 3 }
    , { timespanInSeconds = 60, numberOfMessages = 8 }
    , { timespanInSeconds = 60 * 10, numberOfMessages = 50 }
    ]


decodeBytesToString : Bytes.Bytes -> Maybe String
decodeBytesToString bytes =
    bytes |> Bytes.Decode.decode (Bytes.Decode.string (bytes |> Bytes.width))


encodeStringToBytes : String -> Bytes.Bytes
encodeStringToBytes =
    Bytes.Encode.string >> Bytes.Encode.encode


addCookieUserSessionId : String -> InterfaceToHost.HttpResponse -> InterfaceToHost.HttpResponse
addCookieUserSessionId userSessionId httpResponse =
    let
        cookieHeader =
            { name = "Set-Cookie", values = [ httpSessionIdCookieName ++ "=" ++ userSessionId ++ "; Path=/; Max-Age=3600" ] }
    in
    { httpResponse | headersToAdd = cookieHeader :: httpResponse.headersToAdd }


httpSessionIdCookieName : String
httpSessionIdCookieName =
    "sessionid"


interfaceToHost_initState : State
interfaceToHost_initState =
    { posixTimeMilli = 0
    , conversationHistory = []
    , usersProfiles = Dict.empty
    , usersSessions = Dict.empty
    , usersLastSeen = Dict.empty
    , pendingHttpRequests = Dict.empty
    }
