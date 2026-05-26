// Source: carla/road/MapBuilder.{h,cpp}
//
// Wires the OpenDRIVE parser passes into a fully-populated Map. Parsers call
// the Add*/Create* methods; once all parsers have run, callers invoke Build()
// to assemble the final Map. The builder owns the in-progress MapData and a
// pile of "_temp" containers that the assemble step (Build) drains into the
// final data structures.
//
// Note: a few post-build passes from upstream (CreateJunctionBoundingBoxes,
// ComputeJunctionRoadConflicts, CheckSignalsOnRoads) depend on Wave 3 Map
// methods (GetJunctionWaypoints, ComputeTransform, GetClosestWaypointOnRoad).
// They are intentionally skipped here and will run when Wave 3 wires up the
// Map query layer. The SolveSignalReferencesAndTransforms pass that needs
// Road.GetDirectedPointInNoLaneOffset is partially stubbed: we still wire
// the RoadInfoSignal->Signal pointer + signal->controller links, but skip the
// world-transform computation for non-inertial signals (Wave 3 follow-up).
using System;
using System.Collections.Generic;
using System.Linq;
using CarlaNet.Map.Geom;
using CarlaNet.Map.Road.Element;
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.Road;

public sealed class MapBuilder
{
    private readonly MapData _mapData = new();

    // -- temp staging maps (drained during Build()) -------------------------

    private readonly Dictionary<Road, List<RoadInfo>> _tempRoadInfo = new();
    private readonly Dictionary<Lane, List<RoadInfo>> _tempLaneInfo = new();
    private readonly Dictionary<SignId, Signal> _tempSignals = new();
    private readonly List<RoadInfoSignal> _tempSignalReferences = new();

    // ===== Build ==========================================================

    public Map? Build()
    {
        CreatePointersBetweenRoadSegments();
        RemoveZeroLaneValiditySignalReferences();

        // Move the staged RoadInfo records into per-road / per-lane sets.
        foreach (var kv in _tempRoadInfo)
        {
            kv.Key.Info = new RoadElementSet<RoadInfo>(kv.Value);
        }
        foreach (var kv in _tempLaneInfo)
        {
            // Lane.Info has only a private setter on construction. Use the
            // backing field via reflection... or rather, just expose a setter.
            // We added an internal Reset method below; if absent we throw.
            kv.Key.SetInfo(new RoadElementSet<RoadInfo>(kv.Value));
        }

        SolveSignalReferencesAndTransforms();
        SolveControllerAndJuntionReferences();

        _tempRoadInfo.Clear();
        _tempLaneInfo.Clear();

        // Note: upstream calls CreateJunctionBoundingBoxes / ComputeJunctionRoadConflicts /
        // CheckSignalsOnRoads here. Those need Map's spatial queries (Wave 3).
        var map = new Map(_mapData);
        return map;
    }

    // ===== Setters used by parsers ========================================

    public void SetGeoReference(GeoLocation geoRef)
    {
        _mapData.GeoReference = geoRef;
    }

    // ----- road parser ----------------------------------------------------

    public Road AddRoad(
        RoadId roadId,
        string name,
        double length,
        JuncId junctionId,
        RoadId predecessor,
        RoadId successor,
        bool isRht)
    {
        var road = new Road
        {
            Id = roadId,
            Name = name,
            Length = length,
            JunctionId = junctionId,
            IsJunction = junctionId != -1,
            IsRightHandTraffic = isRht,
            SuccessorRoadId = successor,
            PredecessorRoadId = predecessor,
            MapData = _mapData,
        };
        _mapData.Roads[roadId] = road;
        return road;
    }

    public LaneSection AddRoadSection(Road road, SectionId id, double s)
    {
        var section = new LaneSection(id, s) { Road = road };
        road.LaneSections.Add(section);
        road.LaneSectionsById[id] = section;
        return section;
    }

    public Lane AddRoadSectionLane(
        LaneSection section,
        LaneId laneId,
        uint laneType,
        bool laneLevel,
        LaneId predecessor,
        LaneId successor)
    {
        var lane = new Lane
        {
            Id = laneId,
            Section = section,
            Level = laneLevel,
            Type = (LaneType)(int)laneType,
            Predecessor = predecessor,
            Successor = successor,
        };
        section.Lanes[laneId] = lane;
        return lane;
    }

    public void CreateRoadSpeed(Road road, double s, string type, double max, string unit)
    {
        // Upstream ignores type+unit. Speed.Type defaults to "Town"; we keep that
        // unless the caller actually supplies a type string (most do not).
        var info = string.IsNullOrEmpty(type)
            ? new RoadInfoSpeed(s, max)
            : new RoadInfoSpeed(s, max, type);
        AddRoadInfo(road, info);
    }

    public void CreateSectionOffset(Road road, double s, double a, double b, double c, double d)
    {
        AddRoadInfo(road, new RoadInfoLaneOffset(s, a, b, c, d));
    }

    // ----- geometry parser ------------------------------------------------

    public void AddRoadGeometryLine(Road road, double s, double x, double y, double hdg, double length)
    {
        var loc = new Location((float)x, (float)y, 0f);
        var geo = new GeometryLine(s, length, hdg, loc);
        AddRoadInfo(road, new RoadInfoGeometry(s, geo));
    }

    public void AddRoadGeometryArc(Road road, double s, double x, double y, double hdg, double length, double curvature)
    {
        var loc = new Location((float)x, (float)y, 0f);
        var geo = new GeometryArc(s, length, hdg, loc, curvature);
        AddRoadInfo(road, new RoadInfoGeometry(s, geo));
    }

    public void AddRoadGeometrySpiral(Road road, double s, double x, double y, double hdg, double length,
        double curvStart, double curvEnd)
    {
        var loc = new Location((float)x, (float)y, 0f);
        var geo = new GeometrySpiral(s, length, hdg, loc, curvStart, curvEnd);
        AddRoadInfo(road, new RoadInfoGeometry(s, geo));
    }

    public void AddRoadGeometryPoly3(Road road, double s, double x, double y, double hdg, double length,
        double a, double b, double c, double d)
    {
        var loc = new Location((float)x, (float)y, 0f);
        var geo = new GeometryPoly3(s, length, hdg, loc, a, b, c, d);
        AddRoadInfo(road, new RoadInfoGeometry(s, geo));
    }

    public void AddRoadGeometryParamPoly3(Road road, double s, double x, double y, double hdg, double length,
        double aU, double bU, double cU, double dU,
        double aV, double bV, double cV, double dV,
        string pRange)
    {
        var loc = new Location((float)x, (float)y, 0f);
        var arcLength = pRange == "arcLength";
        var geo = new GeometryParamPoly3(s, length, hdg, loc, aU, bU, cU, dU, aV, bV, cV, dV, arcLength);
        AddRoadInfo(road, new RoadInfoGeometry(s, geo));
    }

    // ----- profiles parser ------------------------------------------------

    public void AddRoadElevationProfile(Road road, double s, double a, double b, double c, double d)
    {
        AddRoadInfo(road, new RoadInfoElevation(s, a, b, c, d));
    }

    // ----- object parser --------------------------------------------------

    public void AddRoadObjectCrosswalk(
        Road road,
        string name,
        double s,
        double t,
        double zOffset,
        double hdg,
        double pitch,
        double roll,
        string orientation,
        double width,
        double length,
        List<CrosswalkPoint> points)
    {
        AddRoadInfo(road, new RoadInfoCrosswalk(s, name, t, zOffset, hdg, pitch, roll, orientation, width, length, points));
    }

    // ----- lane parser ----------------------------------------------------

    public void CreateLaneAccess(Lane lane, double s, string restriction)
        => AddLaneInfo(lane, new RoadInfoLaneAccess(s, restriction));

    public void CreateLaneBorder(Lane lane, double s, double a, double b, double c, double d)
        => AddLaneInfo(lane, new RoadInfoLaneBorder(s, a, b, c, d));

    public void CreateLaneHeight(Lane lane, double s, double inner, double outer)
        => AddLaneInfo(lane, new RoadInfoLaneHeight(s, inner, outer));

    public void CreateLaneMaterial(Lane lane, double s, string surface, double friction, double roughness)
        => AddLaneInfo(lane, new RoadInfoLaneMaterial(s, surface, friction, roughness));

    public void CreateLaneRule(Lane lane, double s, string value)
        => AddLaneInfo(lane, new RoadInfoLaneRule(s, value));

    public void CreateLaneVisibility(Lane lane, double s, double forward, double back, double left, double right)
        => AddLaneInfo(lane, new RoadInfoLaneVisibility(s, forward, back, left, right));

    public void CreateLaneWidth(Lane lane, double s, double a, double b, double c, double d)
        => AddLaneInfo(lane, new RoadInfoLaneWidth(s, a, b, c, d));

    public void CreateLaneSpeed(Lane lane, double s, double max, string unit)
        => AddLaneInfo(lane, new RoadInfoSpeed(s, max));

    public void CreateRoadMark(
        Lane lane,
        int roadMarkId,
        double s,
        string type,
        string weight,
        string color,
        string material,
        double width,
        string laneChange,
        double height,
        string typeName,
        double typeWidth,
        bool isRht)
    {
        var lc = (laneChange?.ToLowerInvariant()) switch
        {
            "increase" => RoadInfoMarkRecord.LaneChangeKind.Increase,
            "decrease" => RoadInfoMarkRecord.LaneChangeKind.Decrease,
            "none"     => RoadInfoMarkRecord.LaneChangeKind.None,
            _          => RoadInfoMarkRecord.LaneChangeKind.Both,
        };
        AddLaneInfo(lane, new RoadInfoMarkRecord(s, roadMarkId, type ?? string.Empty,
            weight ?? string.Empty, color ?? string.Empty, material ?? string.Empty,
            width, lc, height, typeName ?? string.Empty, typeWidth, isRht));
    }

    public void CreateRoadMarkTypeLine(
        Lane lane,
        int roadMarkId,
        double length,
        double space,
        double tOffset,
        double s,
        string rule,
        double width)
    {
        // Find the MarkRecord with this id (last one wins, like upstream's first-match break)
        if (!_tempLaneInfo.TryGetValue(lane, out var list)) return;
        foreach (var info in list)
        {
            if (info is RoadInfoMarkRecord rec && rec.RoadMarkId == roadMarkId)
            {
                rec.Lines.Add(new RoadInfoMarkTypeLine(s, roadMarkId, length, space, tOffset, rule ?? string.Empty, width));
                return;
            }
        }
    }

    // ----- signal parser --------------------------------------------------

    public RoadInfoSignal AddSignal(
        Road road,
        SignId signalId,
        double s,
        double t,
        string name,
        string dynamic,
        string orientation,
        double zOffset,
        string country,
        string type,
        string subtype,
        double value,
        string unit,
        double height,
        double width,
        string text,
        double hOffset,
        double pitch,
        double roll)
    {
        _tempSignals[signalId] = new Signal(
            road.Id, signalId, s, t,
            name ?? string.Empty,
            dynamic ?? string.Empty,
            orientation ?? string.Empty,
            zOffset,
            country ?? string.Empty,
            type ?? string.Empty,
            subtype ?? string.Empty,
            value,
            unit ?? string.Empty,
            height, width,
            text ?? string.Empty,
            hOffset, pitch, roll);

        return AddSignalReference(road, signalId, s, t, orientation ?? string.Empty);
    }

    public void AddSignalPositionInertial(
        SignId signalId,
        double x, double y, double z,
        double hdg, double pitch, double roll)
    {
        if (!_tempSignals.TryGetValue(signalId, out var signal)) return;
        signal.UsingInertialPosition = true;
        var loc = new Location((float)x, (float)-y, (float)z);
        var rot = new Rotation(
            ToDegrees((float)pitch),
            ToDegrees((float)-hdg),
            ToDegrees((float)roll));
        signal.Transform = new Transform(loc, rot);
    }

    public RoadInfoSignal AddSignalReference(
        Road road,
        SignId signalId,
        double sPosition,
        double tPosition,
        string orientation)
    {
        const double epsilon = 0.00001;
        if (sPosition < 0.0) sPosition = 0.0;
        // Prevent s from being equal to road length.
        var fixedS = ClampDouble(sPosition, 0.0, road.Length - epsilon);
        var info = new RoadInfoSignal(signalId, road.Id, fixedS, tPosition, orientation ?? string.Empty);
        AddRoadInfo(road, info);
        _tempSignalReferences.Add(info);
        return info;
    }

    public void AddValidityToSignalReference(RoadInfoSignal signalReference, LaneId fromLane, LaneId toLane)
    {
        signalReference.Validities.Add(new LaneValidity(fromLane, toLane));
    }

    public void AddDependencyToSignal(SignId signalId, string dependencyId, string dependencyType)
    {
        if (!_tempSignals.TryGetValue(signalId, out var signal)) return;
        signal.Dependencies.Add(new SignalDependency(dependencyId, dependencyType));
    }

    // ----- junction parser ------------------------------------------------

    public void AddJunction(JuncId id, string name)
    {
        _mapData.Junctions[id] = new Junction(id, name ?? string.Empty);
    }

    public void AddConnection(JuncId junctionId, ConId connectionId, RoadId incomingRoad, RoadId connectingRoad)
    {
        var j = _mapData.GetJunction(junctionId);
        if (j == null) return;
        j.Connections[connectionId] = new Junction.Connection(connectionId, incomingRoad, connectingRoad);
    }

    public void AddLaneLink(JuncId junctionId, ConId connectionId, LaneId from, LaneId to)
    {
        var j = _mapData.GetJunction(junctionId);
        if (j == null) return;
        if (!j.Connections.TryGetValue(connectionId, out var conn)) return;
        conn.AddLaneLink(from, to);
    }

    public void AddJunctionController(JuncId junctionId, IEnumerable<ContId> controllers)
    {
        var j = _mapData.GetJunction(junctionId);
        if (j == null) return;
        foreach (var c in controllers) j.Controllers.Add(c);
    }

    // ----- controller parser ----------------------------------------------

    public void CreateController(ContId controllerId, string controllerName, uint controllerSequence, IEnumerable<SignId> signals)
    {
        var controller = new Controller(controllerId, controllerName ?? string.Empty, controllerSequence);
        foreach (var s in signals) controller.Signals.Add(s);
        _mapData.Controllers[controllerId] = controller;

        // Upstream also adds the controller_id (mis-typed as `signal` in the C++ loop)
        // to each existing signal's _controllers set. We replicate the actual intent:
        // attach the ContId to every signal the controller owns.
        foreach (var signalId in controller.Signals)
        {
            if (_mapData.Signals.TryGetValue(signalId, out var sig))
            {
                sig.Controllers.Add(controllerId);
            }
        }
    }

    // ----- accessors used by parsers --------------------------------------

    public Road GetRoad(RoadId roadId) => _mapData.GetRoad(roadId);

    /// <summary>
    /// Locate the lane with the given id whose lane-section is active at distance s
    /// along the road. Iterates sections covering s in s-ascending order, returning
    /// the first lane that matches. Mirrors upstream Road::GetLaneByDistance.
    /// </summary>
    public Lane GetLane(RoadId roadId, LaneId laneId, double s)
    {
        var road = _mapData.GetRoad(roadId);
        foreach (var sec in LaneSectionsAt(road, s))
        {
            var lane = sec.GetLane(laneId);
            if (lane != null) return lane;
        }
        throw new InvalidOperationException(
            $"Lane not found: road={roadId} lane={laneId} s={s}");
    }

    // ===== topology fixup =================================================

    private void CreatePointersBetweenRoadSegments()
    {
        // Pass 1: per lane, compute next lanes (and back-fill prev_lanes on each found target).
        foreach (var road in _mapData.Roads.Values)
        {
            foreach (var section in road.LaneSections)
            {
                foreach (var lane in section.Lanes.Values)
                {
                    var nexts = GetLaneNext(road.Id, section.Id, lane.Id);
                    foreach (var nl in nexts)
                    {
                        if (nl == null) continue;
                        lane.NextLanes.Add(nl);
                        nl.PreviousLanes.Add(lane);
                    }
                }
            }
        }

        // Pass 2: for each lane, propagate next/prev to road-level Nexts/Prevs (deduped).
        foreach (var road in _mapData.Roads.Values)
        {
            foreach (var section in road.LaneSections)
            {
                foreach (var lane in section.Lanes.Values)
                {
                    foreach (var nextLane in lane.NextLanes)
                    {
                        var nr = nextLane.Section?.Road;
                        if (nr != null && nr != road && !road.Nexts.Contains(nr))
                        {
                            road.Nexts.Add(nr);
                        }
                    }
                    foreach (var prevLane in lane.PreviousLanes)
                    {
                        var pr = prevLane.Section?.Road;
                        if (pr != null && pr != road && !road.Prevs.Contains(pr))
                        {
                            road.Prevs.Add(pr);
                        }
                    }
                }
            }
        }
    }

    private List<Lane> GetLaneNext(RoadId roadId, SectionId sectionId, LaneId laneId)
    {
        var result = new List<Lane>();
        if (!_mapData.ContainsRoad(roadId)) return result;
        var road = _mapData.GetRoad(roadId);
        if (!road.LaneSectionsById.TryGetValue(sectionId, out var section)) return result;
        var lane = section.GetLane(laneId);
        if (lane == null) return result;

        // Successor or predecessor based on direction (positive lanes go against road s).
        LaneId next;
        RoadId nextRoad;
        if (lane.IsPositiveDirection)
        {
            nextRoad = road.SuccessorRoadId;
            next = lane.Successor;
        }
        else
        {
            nextRoad = road.PredecessorRoadId;
            next = lane.Predecessor;
        }

        var nextIsJunction = !_mapData.ContainsRoad(nextRoad);
        var s = section.S;

        // Determine if we are in a middle section (more sections beyond this one in dir of travel).
        bool middleSection;
        if (!lane.IsPositiveDirection && s > 0)
        {
            middleSection = true;
        }
        else if (lane.IsPositiveDirection && HasUpperBound(road, s))
        {
            middleSection = true;
        }
        else
        {
            middleSection = false;
        }

        if (middleSection)
        {
            // change to next / prev section within the same road
            if (next != 0 || (laneId == 0 && next == 0))
            {
                Lane? target = lane.IsPositiveDirection
                    ? GetRoadNextLane(road, s, next)
                    : GetRoadPrevLane(road, s, next);
                if (target != null) result.Add(target);
            }
        }
        else if (!nextIsJunction)
        {
            // change to another road
            if (next != 0 || (laneId == 0 && next == 0))
            {
                var edge = GetEdgeLanePointer(nextRoad, next);
                if (edge != null) result.Add(edge);
            }
        }
        else
        {
            // junction — multiple possible targets
            var juncId = (JuncId)(int)nextRoad;
            foreach (var (rid, l) in GetJunctionLanes(juncId, roadId, laneId))
            {
                var edge = GetEdgeLanePointer(rid, l.Id);
                if (edge != null) result.Add(edge);
            }
        }

        return result;
    }

    private static bool HasUpperBound(Road road, double s)
    {
        // Mirror upstream's _lane_sections.upper_bound(s) != end():
        // there is at least one section starting strictly after s.
        foreach (var sec in road.LaneSections)
        {
            if (sec.S > s) return true;
        }
        return false;
    }

    private static Lane? GetRoadNextLane(Road road, double s, LaneId laneId)
    {
        // Sections with start strictly > s, in ascending order.
        var ordered = road.LaneSections.OrderBy(x => x.S);
        foreach (var sec in ordered)
        {
            if (sec.S <= s) continue;
            var l = sec.GetLane(laneId);
            if (l != null) return l;
        }
        return null;
    }

    private static Lane? GetRoadPrevLane(Road road, double s, LaneId laneId)
    {
        // Sections strictly < s, in DESCENDING order (reverse iterator on lower_bound).
        var ordered = road.LaneSections.OrderByDescending(x => x.S);
        foreach (var sec in ordered)
        {
            if (sec.S >= s) continue;
            var l = sec.GetLane(laneId);
            if (l != null) return l;
        }
        return null;
    }

    private Lane? GetEdgeLanePointer(RoadId roadId, LaneId laneId)
    {
        if (!_mapData.ContainsRoad(roadId)) return null;
        var road = _mapData.GetRoad(roadId);

        // Reproduce upstream IsPositiveDirection-conditional choice of from_start.
        bool fromStart = (road.IsRightHandTraffic && laneId <= 0) ||
                         (!road.IsRightHandTraffic && laneId >= 0);
        var section = fromStart
            ? GetStartSection(road, laneId)
            : GetEndSection(road, laneId);
        return section?.GetLane(laneId);
    }

    private static LaneSection? GetStartSection(Road road, LaneId laneId)
    {
        foreach (var sec in road.LaneSections.OrderBy(x => x.S))
        {
            if (sec.GetLane(laneId) != null) return sec;
        }
        return null;
    }

    private static LaneSection? GetEndSection(Road road, LaneId laneId)
    {
        foreach (var sec in road.LaneSections.OrderByDescending(x => x.S))
        {
            if (sec.GetLane(laneId) != null) return sec;
        }
        return null;
    }

    private List<(RoadId, Lane)> GetJunctionLanes(JuncId junctionId, RoadId roadId, LaneId laneId)
    {
        var result = new List<(RoadId, Lane)>();
        var junction = _mapData.GetJunction(junctionId);
        if (junction == null) return result;

        foreach (var con in junction.Connections.Values)
        {
            if (!_mapData.Roads.TryGetValue(con.ConnectingRoad, out var connRoad)) continue;
            var connId = connRoad.Id;

            var roadPred = connRoad.PredecessorRoadId;
            var roadSucc = connRoad.SuccessorRoadId;

            if (roadId == roadPred)
            {
                // Lanes at s=0
                foreach (var lane in LanesAt(connRoad, 0.0))
                {
                    if (lane.Predecessor == laneId) result.Add((connId, lane));
                }
            }
            if (roadId == roadSucc)
            {
                foreach (var lane in LanesAt(connRoad, connRoad.Length))
                {
                    if (lane.Successor == laneId) result.Add((connId, lane));
                }
            }
        }
        return result;
    }

    // ===== signal resolution =============================================

    private void SolveSignalReferencesAndTransforms()
    {
        foreach (var sigRef in _tempSignalReferences)
        {
            if (_tempSignals.TryGetValue(sigRef.SignalId, out var sig))
            {
                sigRef.Signal = sig;
            }
        }

        // Compute signal world transforms. Inertial-positioned signals already have
        // their transform set. Road-relative signals need Road.GetDirectedPointInNoLaneOffset,
        // which is Wave 3 — until that exists, we leave Signal.Transform default.
        // Move temp signals into MapData.
        foreach (var kv in _tempSignals)
        {
            _mapData.Signals[kv.Key] = kv.Value;
        }
        _tempSignals.Clear();

        GenerateDefaultValiditiesForSignalReferences();
    }

    private void SolveControllerAndJuntionReferences()
    {
        foreach (var junction in _mapData.Junctions.Values)
        {
            foreach (var controllerId in junction.Controllers)
            {
                if (!_mapData.Controllers.TryGetValue(controllerId, out var ctrl)) continue;
                ctrl.Junctions.Add(junction.Id);
                foreach (var sigId in ctrl.Signals)
                {
                    if (_mapData.Signals.TryGetValue(sigId, out var sig))
                    {
                        sig.Controllers.Add(controllerId);
                    }
                }
            }
        }
    }

    private void GenerateDefaultValiditiesForSignalReferences()
    {
        foreach (var sigRef in _tempSignalReferences)
        {
            if (sigRef.Validities.Count != 0) continue;

            var road = GetRoad(sigRef.RoadId);
            var lanes = LanesAt(road, sigRef.S).ToList();
            switch (sigRef.Orientation)
            {
                case SignalOrientation.Positive:
                {
                    LaneId minLane = 1;
                    LaneId maxLane = 0;
                    foreach (var l in lanes)
                    {
                        if (l.Id > maxLane) maxLane = l.Id;
                    }
                    if (minLane <= maxLane)
                        AddValidityToSignalReference(sigRef, minLane, maxLane);
                    break;
                }
                case SignalOrientation.Negative:
                {
                    LaneId minLane = 0;
                    LaneId maxLane = -1;
                    foreach (var l in lanes)
                    {
                        if (l.Id < minLane) minLane = l.Id;
                    }
                    if (minLane <= maxLane)
                        AddValidityToSignalReference(sigRef, minLane, maxLane);
                    break;
                }
                case SignalOrientation.Both:
                {
                    // positive side
                    LaneId minLane = 1;
                    LaneId maxLane = 0;
                    foreach (var l in lanes)
                    {
                        if (l.Id > maxLane) maxLane = l.Id;
                    }
                    if (minLane <= maxLane)
                        AddValidityToSignalReference(sigRef, minLane, maxLane);

                    // negative side
                    minLane = 0;
                    maxLane = -1;
                    foreach (var l in lanes)
                    {
                        if (l.Id < minLane) minLane = l.Id;
                    }
                    if (minLane <= maxLane)
                        AddValidityToSignalReference(sigRef, minLane, maxLane);
                    break;
                }
            }
        }
    }

    private void RemoveZeroLaneValiditySignalReferences()
    {
        var toRemove = new List<RoadInfoSignal>();
        foreach (var sigRef in _tempSignalReferences)
        {
            bool shouldRemove = true;
            foreach (var v in sigRef.Validities)
            {
                if (v.FromLane != 0 || v.ToLane != 0) { shouldRemove = false; break; }
            }
            if (sigRef.Validities.Count == 0) shouldRemove = false;
            if (shouldRemove) toRemove.Add(sigRef);
        }
        foreach (var el in toRemove)
        {
            var road = GetRoad(el.RoadId);
            if (_tempRoadInfo.TryGetValue(road, out var infoList))
            {
                infoList.RemoveAll(x => ReferenceEquals(x, el));
            }
            _tempSignalReferences.RemoveAll(x => ReferenceEquals(x, el));
        }
    }

    // ===== helpers =======================================================

    private void AddRoadInfo(Road road, RoadInfo info)
    {
        if (!_tempRoadInfo.TryGetValue(road, out var list))
        {
            list = new List<RoadInfo>();
            _tempRoadInfo[road] = list;
        }
        list.Add(info);
    }

    private void AddLaneInfo(Lane lane, RoadInfo info)
    {
        if (!_tempLaneInfo.TryGetValue(lane, out var list))
        {
            list = new List<RoadInfo>();
            _tempLaneInfo[lane] = list;
        }
        list.Add(info);
    }

    // Sections whose start s is <= the query and that "cover" the query. Upstream's
    // GetLessEqualRange uses the multimap equal_range trick: find the s-value of the
    // largest section start <= the query, then return all sections with that exact s.
    private static IEnumerable<LaneSection> LaneSectionsAt(Road road, double s)
    {
        if (road.LaneSections.Count == 0) yield break;

        double target = double.NegativeInfinity;
        foreach (var sec in road.LaneSections)
        {
            if (sec.S <= s && sec.S > target) target = sec.S;
        }
        if (target == double.NegativeInfinity) yield break;

        foreach (var sec in road.LaneSections)
        {
            if (sec.S == target) yield return sec;
        }
    }

    private static IEnumerable<Lane> LanesAt(Road road, double s)
    {
        foreach (var sec in LaneSectionsAt(road, s))
        {
            foreach (var lane in sec.Lanes.Values)
            {
                yield return lane;
            }
        }
    }

    private static double ClampDouble(double v, double min, double max)
        => v < min ? min : (v > max ? max : v);

    private static float ToDegrees(float radians) => radians * (180f / MathF.PI);
}
