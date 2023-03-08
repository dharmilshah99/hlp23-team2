﻿module SmartWire

open CommonTypes
open Elmish
open DrawHelpers
open DrawModelType.SymbolT
open DrawModelType.BusWireT
open BusWire
open BusWireUpdateHelpers
open SmartHelpers

open Optics
open Operators

(* HLP23

    This module will normally be used exclusively by team member doing the "smart autoroute on single
    wire creation" part of the individual coding. During group phase work how it is used is up to the
    group. Functions from other members MUST be documented by "HLP23: AUTHOR" XML 
    comment as in SmartHelpers.

    It does not need specific "get started" test code since is called whenever a new wire is created 
    or (not usual) a symbol is moved so far that the topology of a wire chnages and it is autorouted 
    again from scratch.

*)

(* HLP23 AUTHOR: Jian Fu Eng (jfe20)
NOTE:   For ease of understanding, algorithm, variable names and documentation of code below are all explained 
        in the simple case of no rotated symbols (ie wire.InitialOrientation = Horizontal).

        However, the code implemented supports the rotated case as well.

Implemented the following Smart Routing Algorithm:

    1)  Check if initial autorouted wire has any intersections with symbols. 
        If yes, calculate the bounding boxes of all the intersected symbols.
    2)  Attempt to shift the vertical seg of the 7 seg wire to buffer amount left of the left most
        bound of the intersected symbols. 
        If there are still intersections, try shifting to the right most bound + buffer.
    3)  If there are still intersections, recursively try to shift the horizontal seg of the 7 seg 
        or 9 seg wire to either the top or bottom most bound of the intersected symbols. 
        If both shifted wires still result in an intersection, compute the vertical distances between 
        the start/end pos of the wire and the top/bottom bound of the intersected symbols. 
        Using the 4 vertical distances computed, decide whether to try shifting the wire up or down 
        depending on which results in a wire with shorter vertical distance.
        
        A max recursion depth is defined for step 3 so that Issie will not break when there are physically 
        no possible routes that will not intersect any symbol (eg when dragging a symbol around such that 
        the dragged symbol is within another symbol) or when there are special corner cases that have not 
        been implemented yet (eg symbol A is in top left quadrant with input port facing up, connected to
        symbol B in bottom right quadrant with output port facing down, with other symbols in between the
        2 symbols).

*)

module Constants =
    let buffer = 10.
    let maxCallsToShiftHorizontalSeg = 5
    let minWireSeparation = 10.

//------------------------------------------------------------------------//
//--------------------------Shifting Vertical Segment---------------------//
//------------------------------------------------------------------------//

/// Try shifting vertical seg to either - buffer or + buffer of intersected symbols.
/// Returns None if no route found.
let tryShiftVerticalSeg (model: Model) (intersectedBoxes: (ComponentId * BoundingBox) list) (wire: Wire) : Wire option =
    let wireVertices =
        segmentsToIssieVertices wire.Segments wire
        |> List.map (fun (x, y, _) -> { X = x; Y = y })

    let currentVerticalSegXPos =
        match wire.InitialOrientation with
        | Horizontal -> wireVertices[4].X
        | Vertical -> wireVertices[4].Y

    let shiftVerticalSeg amountToShift =
        let prevSeg = wire.Segments[2]
        let nextSeg = wire.Segments[4]
        let newPrevSeg = { prevSeg with Length = prevSeg.Length + amountToShift }
        let newNextSeg = { nextSeg with Length = nextSeg.Length - amountToShift }

        let newSegments =
            wire.Segments[..1]
            @ [ newPrevSeg ] @ wire.Segments[3..3] @ [ newNextSeg ] @ wire.Segments[5..]

        { wire with Segments = newSegments }

    let tryShiftLeftWire =
        let leftBound =
            intersectedBoxes
            |> List.map (fun (_compID, box) ->
                match wire.InitialOrientation with
                | Horizontal -> box.TopLeft.X
                | Vertical -> box.TopLeft.Y)
            |> List.min

        let amountToShift = currentVerticalSegXPos - leftBound + Constants.buffer
        shiftVerticalSeg -amountToShift

    let tryShiftRightWire =
        let rightBound =
            intersectedBoxes
            |> List.map (fun (_compID, box) ->
                match wire.InitialOrientation with
                | Horizontal -> box.TopLeft.X + box.W
                | Vertical -> box.TopLeft.Y + box.H)
            |> List.max

        let amountToShift = rightBound - currentVerticalSegXPos + Constants.buffer
        shiftVerticalSeg amountToShift

    let leftShiftedWireIntersections =
        findWireSymbolIntersections model tryShiftLeftWire

    let rightShiftedWireIntersections =
        findWireSymbolIntersections model tryShiftRightWire

    // Check which newly generated wire has no intersections, return that
    match leftShiftedWireIntersections, rightShiftedWireIntersections with
    | [], _ -> Some tryShiftLeftWire
    | _, [] -> Some tryShiftRightWire
    | _, _ -> None

//------------------------------------------------------------------------//
//-------------------------Shifting Horizontal Segment--------------------//
//------------------------------------------------------------------------//
type VertDistFromBoundingBox =
    | Above of float // Vertical distance between pos and a bounding box above
    | Below of float // Vertical distance between pos and a bounding box below

/// Check if any bounding box is directly above or below startPos and endPos.
/// If yes, returns a tuple of form:
/// distance between pos and the furthest box above, distance between pos and the furthest box below
let isBoundingBoxAboveOrBelowPos
    (intersectedBoxes: (ComponentId * BoundingBox) list)
    (pos: XYPos)
    (wireOrientation: Orientation)
    : float * float =

    let getVertDistanceToBox (pos: XYPos) (box: BoundingBox) : VertDistFromBoundingBox option =
        match wireOrientation with
        | Horizontal ->
            if inMiddleOrEndOf box.TopLeft.X pos.X (box.TopLeft.X + box.W) then
                if pos.Y > box.TopLeft.Y then
                    pos.Y - box.TopLeft.Y |> Above |> Some
                else
                    box.TopLeft.Y - pos.Y + box.H |> Below |> Some
            else
                None
        | Vertical ->
            if inMiddleOrEndOf box.TopLeft.Y pos.Y (box.TopLeft.Y + box.H) then
                if pos.X > box.TopLeft.X then
                    pos.X - box.TopLeft.X |> Above |> Some
                else
                    box.TopLeft.X - pos.X + box.W |> Below |> Some
            else
                None

    let verticalDistances =
        intersectedBoxes
        |> List.map (fun (_compID, box) -> getVertDistanceToBox pos box)
        |> List.filter (fun x -> x <> None)
        |> List.map (Option.get)

    // Recursively extracts largest distance above and below pos from list of distances
    let rec largestDistance verticalDistances (currentLargestAbove, currentLargestBelow) =
        match verticalDistances with
        | [] -> currentLargestAbove, currentLargestBelow
        | Above d :: rest ->
            if d > currentLargestAbove then
                largestDistance rest (d, currentLargestBelow)
            else
                largestDistance rest (currentLargestAbove, currentLargestBelow)
        | Below d :: rest ->
            if d > currentLargestBelow then
                largestDistance rest (currentLargestAbove, d)
            else
                largestDistance rest (currentLargestAbove, currentLargestBelow)

    largestDistance verticalDistances (0., 0.)

type DirectionToMove =
    | Up_
    | Down_
    | Left_
    | Right_

let updatePos (pos: XYPos) (direction: DirectionToMove) (distanceToShift: float) : XYPos =
    match direction with
    | Up_ -> { pos with Y = pos.Y - distanceToShift }
    | Down_ -> { pos with Y = pos.Y + distanceToShift }
    | Left_ -> { pos with X = pos.X - distanceToShift }
    | Right_ -> { pos with X = pos.X + distanceToShift }

let rec findMinWireSeparation (model: Model) (pos: XYPos) (wire: Wire) (direction: DirectionToMove) =
    let box =
        { TopLeft = updatePos pos direction (Constants.minWireSeparation / 2.)
          W = Constants.minWireSeparation * 2.
          H = Constants.minWireSeparation * 2. }

    let intersectingWires = getWiresInBox box model

    match intersectingWires with
    | [] -> pos
    | [ w ] when w.WId = wire.WId -> pos
    | _ ->
        let newPos = updatePos pos direction Constants.minWireSeparation
        findMinWireSeparation model newPos wire direction

/// Recursively shift horizontal seg up/down until no symbol intersections.
/// Limit in recursion depth defined by argument callsLeft given to initial function call.
/// Limit needed to prevent Issie from breaking when there are physically
/// no possible routes that achieve 0 intersections.
/// Returns None if no route found
let rec tryShiftHorizontalSeg
    (model: Model)
    (intersectedBoxes: (ComponentId * BoundingBox) list)
    (wire: Wire)
    (callsLeft: int)
    : Wire option =
    if callsLeft = 0 then
        None
    else
        let currentStartPos, currentEndPos = getStartAndEndWirePos wire

        let shiftWireHorizontally firstVerticalSegLength secondVerticalSegLength =
            let newSegments =
                match wire.Segments.Length with
                | 5 ->
                    wire.Segments[..0]
                    @ [ { wire.Segments[1] with Length = firstVerticalSegLength } ]
                      @ [ { wire.Segments[2] with Length = wire.Segments[2].Length } ]
                        @ [ { wire.Segments[3] with Length = secondVerticalSegLength } ]
                          @ wire.Segments[4..]
                | 6 ->
                    // Change segments index 1,3. Leave rest as is
                    wire.Segments[..0]
                    @ [ { wire.Segments[1] with Length = firstVerticalSegLength } ]
                      @ wire.Segments[2..2]
                        @ [ { wire.Segments[3] with Length = secondVerticalSegLength } ]
                          @ wire.Segments[4..]
                | 7 ->
                    // Change into a 5 segment wire
                    wire.Segments[..0]
                    @ [ { wire.Segments[1] with Length = firstVerticalSegLength } ]
                      @ [ { wire.Segments[2] with Length = wire.Segments[2].Length + wire.Segments[4].Length } ]
                        @ [ { wire.Segments[3] with Length = secondVerticalSegLength } ]
                          @ [ { wire.Segments[6] with Index = 4 } ]
                | 9 ->
                    // Change segments index 1,3,5,7. Leave rest as is
                    wire.Segments[..0]
                    @ [ { wire.Segments[1] with Length = 0. } ]
                      @ wire.Segments[2..2]
                        @ [ { wire.Segments[3] with Length = firstVerticalSegLength } ]
                          @ wire.Segments[4..4]
                            @ [ { wire.Segments[5] with Length = secondVerticalSegLength } ]
                              @ wire.Segments[6..6]
                                @ [ { wire.Segments[7] with Length = 0. } ] @ wire.Segments[8..]
                | _ -> wire.Segments

            { wire with Segments = newSegments }

        let tryShiftUpWire =
            let topBoundBox =
                intersectedBoxes
                |> List.sortWith (fun (_, box1) (_, box2) ->
                    match wire.InitialOrientation with
                    | Horizontal -> compare box1.TopLeft.Y box2.TopLeft.Y
                    | Vertical -> compare box1.TopLeft.X box2.TopLeft.X)
                |> List.head
                |> snd

            let topBound =
                let viablePos =
                    match wire.InitialOrientation with
                    | Horizontal ->
                        let initialAttemptPos = updatePos topBoundBox.TopLeft Up_ Constants.buffer
                        findMinWireSeparation model initialAttemptPos wire Up_
                    | Vertical ->
                        let initialAttemptPos = updatePos topBoundBox.TopLeft Left_ Constants.buffer
                        findMinWireSeparation model initialAttemptPos wire Left_

                match wire.InitialOrientation with
                | Horizontal -> viablePos.Y
                | Vertical -> viablePos.X

            let firstVerticalSegLength, secondVerticalSegLength =
                match wire.InitialOrientation with
                | Horizontal -> topBound - currentStartPos.Y, currentEndPos.Y - (topBound)
                | Vertical -> topBound - currentStartPos.X, currentEndPos.X - (topBound)

            shiftWireHorizontally firstVerticalSegLength secondVerticalSegLength

        let tryShiftDownWire =
            let bottomBoundBox =
                intersectedBoxes
                |> List.sortWith (fun (_, box1) (_, box2) ->
                    match wire.InitialOrientation with
                    | Horizontal -> compare (box1.TopLeft.Y + box1.H) (box2.TopLeft.Y + box2.H)
                    | Vertical -> compare (box1.TopLeft.X + box1.W) (box2.TopLeft.X + box2.W))
                |> List.rev
                |> List.head
                |> snd

            let bottomBound =
                match wire.InitialOrientation with
                | Horizontal -> bottomBoundBox.TopLeft.Y + bottomBoundBox.H
                | Vertical -> bottomBoundBox.TopLeft.X + bottomBoundBox.W

            let firstVerticalSegLength, secondVerticalSegLength =
                match wire.InitialOrientation with
                | Horizontal ->
                    bottomBound + Constants.buffer - currentStartPos.Y,
                    currentEndPos.Y - (bottomBound + Constants.buffer)
                | Vertical ->
                    bottomBound + Constants.buffer - currentStartPos.X,
                    currentEndPos.X - (bottomBound + Constants.buffer)

            shiftWireHorizontally firstVerticalSegLength secondVerticalSegLength

        let upShiftedWireIntersections = findWireSymbolIntersections model tryShiftUpWire

        let downShiftedWireIntersections =
            findWireSymbolIntersections model tryShiftDownWire

        // If newly generated wire has no intersections, return that
        // Otherwise, decide to shift up or down based on which is closer
        match upShiftedWireIntersections, downShiftedWireIntersections with
        | [], [] ->
            if getWireLength tryShiftDownWire < getWireLength tryShiftUpWire then
                Some tryShiftDownWire
            else
                Some tryShiftUpWire
        | [], _ -> Some tryShiftUpWire
        | _, [] -> Some tryShiftDownWire
        | _, _ ->
            let (distanceAboveFromStart, distanceBelowFromStart) =
                isBoundingBoxAboveOrBelowPos intersectedBoxes currentStartPos wire.InitialOrientation

            let (distanceAboveFromEnd, distanceBelowFromEnd) =
                isBoundingBoxAboveOrBelowPos intersectedBoxes currentEndPos wire.InitialOrientation

            match max distanceAboveFromStart distanceAboveFromEnd, max distanceBelowFromStart distanceBelowFromEnd with
            | distanceFromAbove, distanceFromBelow when distanceFromAbove > distanceFromBelow ->
                tryShiftHorizontalSeg model downShiftedWireIntersections tryShiftDownWire (callsLeft - 1)
            | _distanceFromAbove, _distanceFromBelow (*when _distanceFromAbove <= _distanceFromBelow*)  ->
                tryShiftHorizontalSeg model upShiftedWireIntersections tryShiftUpWire (callsLeft - 1)

//------------------------------------------------------------------------//
//-----------------------------Snapping to Net----------------------------//
//------------------------------------------------------------------------//

let getWireVertices (wire: Wire) =
    segmentsToIssieVertices wire.Segments wire
    |> List.map (fun (x, y, _) -> { X = x; Y = y })

let copySegments (wire: Wire) (refWire: Wire) (numOfSegsToCopy: int) =
    [ 0 .. numOfSegsToCopy - 1 ]
    |> List.map (fun i -> { wire.Segments[i] with Length = refWire.Segments[i].Length })

let snapToNet (model: Model) (wireToRoute: Wire) : Wire =
    match isWireInNet model wireToRoute, wireToRoute.Segments.Length with
    | None, _ -> wireToRoute // If wire is not in net, return original wire
    | _, n when n <> 5 && n <> 7 -> wireToRoute // If wire is not 5 or 7 seg, return original wire
    | Some(_, netlist), _ ->
        // Take first wire in netlist as reference wire for snapping
        let refWire = netlist.Head |> snd
        let refWireVertices = getWireVertices refWire

        let wireToRouteStartPos, wireToRouteEndPos = getStartAndEndWirePos wireToRoute
        let _, refEndPos = getStartAndEndWirePos refWire

        let firstBendPos = refWireVertices[3]
        let horizontalSegLength = refWire.Segments[2].Length

        let isHorizontalSegTooShort =
            (wireToRouteEndPos.X - wireToRouteStartPos.X) < horizontalSegLength / 2.

        let numOfSegsToCopy =

            let simpleCase =
                match wireToRouteEndPos.X < firstBendPos.X, isHorizontalSegTooShort with
                | true, true -> 1
                | true, false -> 2
                | false, _ -> 3

            match refWire.Segments.Length with
            | 5 ->
                match firstBendPos.Y < refEndPos.Y, firstBendPos.Y > wireToRouteEndPos.Y with
                | (true, true)
                | (false, false) -> if wireToRouteEndPos.X < firstBendPos.X then 2 else 3
                | _ -> simpleCase
            | 7 -> simpleCase
            | _ -> 0 // Not implemented for ref wires that are not 5 or 7 seg

        let newSegments =
            match numOfSegsToCopy with
            | 3 ->
                copySegments wireToRoute refWire 3
                @ [ { wireToRoute.Segments[3] with Length = wireToRouteEndPos.Y - firstBendPos.Y } ]
                  @ [ { wireToRoute.Segments[4] with Length = wireToRouteEndPos.X - firstBendPos.X } ]
                    @ [ { wireToRoute.Segments[3] with
                            Index = 5
                            Length = 0. } ]
                      @ [ { wireToRoute.Segments[4] with
                              Index = 6
                              Length = nubLength } ]
            | 2 ->
                copySegments wireToRoute refWire 2
                @ [ { wireToRoute.Segments[2] with Length = wireToRouteEndPos.X - wireToRouteStartPos.X - nubLength } ]
                  @ [ { wireToRoute.Segments[3] with Length = wireToRouteEndPos.Y - firstBendPos.Y } ]
                    @ [ { wireToRoute.Segments[4] with
                            Index = 4
                            Length = 0. } ]
                      @ [ { wireToRoute.Segments[3] with
                              Index = 5
                              Length = 0. } ]
                        @ [ { wireToRoute.Segments[4] with
                                Index = 6
                                Length = nubLength } ]
            | 1 ->
                copySegments wireToRoute refWire 1
                @ [ { wireToRoute.Segments[1] with Length = wireToRouteEndPos.Y - wireToRouteStartPos.Y } ]
                  @ [ { wireToRoute.Segments[2] with Length = wireToRouteEndPos.X - wireToRouteStartPos.X - nubLength } ]
                    @ [ { wireToRoute.Segments[3] with Length = 0. } ]
                      @ [ { wireToRoute.Segments[4] with
                              Index = 4
                              Length = 0. } ]
                        @ [ { wireToRoute.Segments[3] with
                                Index = 5
                                Length = 0. } ]
                          @ [ { wireToRoute.Segments[4] with
                                  Index = 6
                                  Length = nubLength } ]
            | 0 -> wireToRoute.Segments // Not implemented for ref wires that are not 5 or 7 seg
            | _ -> failwithf "Shouldn't happen"

        { wireToRoute with Segments = newSegments }


/// top-level function which replaces autoupdate and implements a smarter version of same
/// it is called every time a new wire is created, so is easily tested.
let smartAutoroute (model: Model) (wire: Wire) : Wire =

    let initialWire = autoroute model wire

    // Snapping to Net only implemented for one orientation
    let snappedToNetWire =
        match wire.InitialOrientation with
        | Horizontal -> snapToNet model initialWire
        | Vertical -> initialWire

    let intersectedBoxes = findWireSymbolIntersections model snappedToNetWire

    match intersectedBoxes.Length with
    | 0 -> snappedToNetWire
    | _ ->
        tryShiftVerticalSeg model intersectedBoxes snappedToNetWire
        |> Option.orElse (
            tryShiftHorizontalSeg model intersectedBoxes snappedToNetWire Constants.maxCallsToShiftHorizontalSeg
        )
        |> Option.defaultValue snappedToNetWire
