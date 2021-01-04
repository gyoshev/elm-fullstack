module ProjectState exposing (..)

import Bytes
import Bytes.Encode
import Bytes.Extra
import Dict
import List
import Pine
import SHA256


type alias ProjectState =
    ProjectState_2020_12


type alias ProjectState_2020_12 =
    FileTreeNode


type FileTreeNode
    = BlobNode Bytes.Bytes
    | TreeNode TreeNodeStructure


type alias TreeNodeStructure =
    List TreeNodeEntryStructure


type alias TreeNodeEntryStructure =
    ( String, FileTreeNode )


compositionPineValueFromFileTreeNode : FileTreeNode -> Pine.Value
compositionPineValueFromFileTreeNode treeNode =
    case treeNode of
        BlobNode bytes ->
            Pine.BlobValue (Bytes.Extra.toByteValues bytes)

        TreeNode entries ->
            entries
                |> List.map
                    (\( entryName, entryValue ) ->
                        Pine.ListValue [ Pine.valueFromString entryName, compositionPineValueFromFileTreeNode entryValue ]
                    )
                |> Pine.ListValue


compositionHashFromFileTreeNode : FileTreeNode -> SHA256.Digest
compositionHashFromFileTreeNode =
    compositionPineValueFromFileTreeNode >> Pine.hashDigestFromValue


flatListOfBlobsFromFileTreeNode : FileTreeNode -> List ( List String, Bytes.Bytes )
flatListOfBlobsFromFileTreeNode treeNode =
    case treeNode of
        BlobNode blob ->
            [ ( [], blob ) ]

        TreeNode treeEntries ->
            treeEntries
                |> List.concatMap
                    (\( childName, childContent ) ->
                        childContent
                            |> flatListOfBlobsFromFileTreeNode
                            |> List.map (Tuple.mapFirst ((::) childName))
                    )


sortedFileTreeFromListOfBlobs : List ( List String, Bytes.Bytes ) -> FileTreeNode
sortedFileTreeFromListOfBlobs =
    List.foldl setBlobAtPathInSortedFileTree (TreeNode [])


getBlobAtPathFromFileTree : List String -> FileTreeNode -> Maybe Bytes.Bytes
getBlobAtPathFromFileTree path treeNode =
    case getNodeAtPathFromFileTree path treeNode of
        Just (BlobNode blob) ->
            Just blob

        _ ->
            Nothing


getNodeAtPathFromFileTree : List String -> FileTreeNode -> Maybe FileTreeNode
getNodeAtPathFromFileTree path treeNode =
    case path of
        [] ->
            Just treeNode

        pathFirstElement :: pathRest ->
            case treeNode of
                BlobNode _ ->
                    Nothing

                TreeNode treeElements ->
                    case treeElements |> List.filter (Tuple.first >> (==) pathFirstElement) |> List.head of
                        Nothing ->
                            Nothing

                        Just ( _, subNode ) ->
                            getNodeAtPathFromFileTree pathRest subNode


setBlobAtPathInSortedFileTree : ( List String, Bytes.Bytes ) -> FileTreeNode -> FileTreeNode
setBlobAtPathInSortedFileTree ( path, blobContent ) stateBefore =
    case path of
        [] ->
            BlobNode blobContent

        pathFirstElement :: pathRest ->
            let
                nodeBefore =
                    getNodeAtPathFromFileTree [ pathFirstElement ] stateBefore

                node =
                    nodeBefore
                        |> Maybe.withDefault (BlobNode (Bytes.Encode.encode (Bytes.Encode.string "")))
                        |> setBlobAtPathInSortedFileTree ( pathRest, blobContent )

                treeEntriesBefore =
                    case stateBefore of
                        BlobNode _ ->
                            []

                        TreeNode treeBeforeEntries ->
                            treeBeforeEntries

                treeEntries =
                    treeEntriesBefore
                        |> Dict.fromList
                        |> Dict.insert pathFirstElement node
                        |> Dict.toList
            in
            TreeNode treeEntries
