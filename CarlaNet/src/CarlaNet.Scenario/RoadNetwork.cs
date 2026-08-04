using CarlaNet.Map.OpenDrive;
using CarlaNet.Map.Road.Element;
using CarlaNet.Types.Geom;

using RoadMap = CarlaNet.Map.Road.Map;

namespace CarlaNet.Scenario;

/// <summary>
/// Turns the road-referenced positions a storyboard uses into world poses.
///
/// A scenario places entities by road, lane and distance along that road, which only means anything
/// against the network actually loaded. The authoring tool preserves the identifiers of the network it
/// imported, so a scenario authored against a generated world resolves against that same world without
/// a translation step — but the identifiers are stable only for a given build, and a scenario run
/// against a differently generated world will resolve to the wrong place rather than fail. Confirming
/// that a scenario belongs to the loaded world is the caller's responsibility.
/// </summary>
public sealed class RoadNetwork
{
    private readonly RoadMap _map;

    private RoadNetwork(RoadMap map) => _map = map;

    /// <summary>Parse an OpenDRIVE document, normally the one the server reports as loaded.</summary>
    public static RoadNetwork FromOpenDrive(string openDriveXml)
    {
        RoadMap map = OpenDriveParser.Load(openDriveXml)
            ?? throw new ScenarioParseException("the loaded road network could not be parsed");
        return new RoadNetwork(map);
    }

    /// <summary>
    /// World pose for a lane position, raised clear of the surface so a vehicle is not placed
    /// intersecting the road it stands on.
    ///
    /// The default matches the height CARLA itself uses for the spawn points it generates from a road
    /// network (SpawnersHeight, three metres). The margin has to exceed the vehicle's own half-height,
    /// which is around 0.7 m for a saloon: a smaller offset places the underside below the surface, and
    /// physics then resolves the intersection unpredictably — the vehicle may settle, or be pinned, or
    /// be thrown clear — which makes placement a race rather than a placement.
    /// </summary>
    public Transform Resolve(LanePosition position, double heightAboveSurface = 3.0)
    {
        RoadId roadId = checked((RoadId)position.RoadId);
        if (!_map.Data.Roads.TryGetValue(roadId, out var road))
            throw new ScenarioParseException(
                $"road {position.RoadId} is not in the loaded network; the scenario was authored " +
                "against a different world");

        // Resolve the lane section explicitly rather than leaving it to be inferred. A road may carry
        // several sections and their identifiers need not start at the beginning of the road, so a
        // guessed identifier can silently select a section that does not contain this distance.
        var section = road.LaneSections.Count == 0 ? null : road.LaneSections[0];
        foreach (var candidate in road.LaneSections)
        {
            if (candidate.S <= position.S) section = candidate;
            else break;
        }
        if (section is null)
            throw new ScenarioParseException($"road {position.RoadId} has no lane sections");

        if (section.GetLane(position.LaneId) is null)
            throw new ScenarioParseException(
                $"lane {position.LaneId} does not exist on road {position.RoadId} at s={position.S:0.##}");

        if (position.S < 0 || position.S > road.Length)
            throw new ScenarioParseException(
                $"s={position.S:0.##} lies outside road {position.RoadId}, which is " +
                $"{road.Length:0.##} m long");

        Transform t = _map.ComputeTransform(
            new Waypoint(roadId, section.Id, position.LaneId, position.S));

        // The lateral offset a storyboard may carry is applied along the lane's normal, which is the
        // heading rotated a quarter turn.
        if (position.Offset != 0.0)
        {
            double normal = (t.Rotation.Yaw + 90.0) * Math.PI / 180.0;
            t = new Transform(
                new Location(
                    (float)(t.Location.X + Math.Cos(normal) * position.Offset),
                    (float)(t.Location.Y + Math.Sin(normal) * position.Offset),
                    t.Location.Z),
                t.Rotation);
        }

        return new Transform(
            new Location(t.Location.X, t.Location.Y, (float)(t.Location.Z + heightAboveSurface)),
            t.Rotation);
    }
}
