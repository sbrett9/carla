// Source: carla/opendrive/parser/RoadParser.{h,cpp}
//
// Parses every <road> element: metadata (id/name/length/junction), link
// (predecessor/successor at the road level), road-type/speed records, lane
// offsets, lane-section structure (left/center/right lane lists with
// per-lane link info). Lane-internal records (width, mark, etc.) come later
// from LaneParser.
using System.Xml.Linq;
using CarlaNet.Map.Road;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class RoadParser
{
    public static void Parse(XDocument xml, MapBuilder builder)
    {
        var root = xml.Root;
        if (root == null) return;

        foreach (var roadNode in root.Elements("road"))
        {
            var id = XmlExt.AsUInt(roadNode.Attribute("id"));
            var name = XmlExt.AsString(roadNode.Attribute("name"));
            var length = XmlExt.AsDouble(roadNode.Attribute("length"));
            var junctionId = XmlExt.AsInt(roadNode.Attribute("junction"), -1);

            var ruleAttr = roadNode.Attribute("rule");
            // Default rule is RHT when missing per upstream.
            bool isRht = ruleAttr == null || ruleAttr.Value == "RHT";

            RoadId predecessor = 0;
            RoadId successor = 0;
            var link = roadNode.Element("link");
            if (link != null)
            {
                var pred = link.Element("predecessor");
                if (pred != null) predecessor = XmlExt.AsUInt(pred.Attribute("elementId"));
                var succ = link.Element("successor");
                if (succ != null) successor = XmlExt.AsUInt(succ.Attribute("elementId"));
            }

            var road = builder.AddRoad(id, name, length, junctionId, predecessor, successor, isRht);

            // type / speed entries
            foreach (var typeNode in roadNode.Elements("type"))
            {
                var s = XmlExt.AsDouble(typeNode.Attribute("s"));
                var type = XmlExt.AsString(typeNode.Attribute("type"));
                double max = 0.0;
                string unit = string.Empty;
                var speedNode = typeNode.Element("speed");
                if (speedNode != null)
                {
                    max = XmlExt.AsDouble(speedNode.Attribute("max"));
                    unit = XmlExt.AsString(speedNode.Attribute("unit"));
                }
                builder.CreateRoadSpeed(road, s, type, max, unit);
            }

            // section offsets (<lanes><laneOffset>)
            var lanesNode = roadNode.Element("lanes");
            int offsetCount = 0;
            if (lanesNode != null)
            {
                foreach (var off in lanesNode.Elements("laneOffset"))
                {
                    builder.CreateSectionOffset(road,
                        XmlExt.AsDouble(off.Attribute("s")),
                        XmlExt.AsDouble(off.Attribute("a")),
                        XmlExt.AsDouble(off.Attribute("b")),
                        XmlExt.AsDouble(off.Attribute("c")),
                        XmlExt.AsDouble(off.Attribute("d")));
                    offsetCount++;
                }
            }
            if (offsetCount == 0)
            {
                // Match upstream's "add a default lane offset if none is found".
                builder.CreateSectionOffset(road, 0.0, 0.0, 0.0, 0.0, 0.0);
            }

            // lane sections
            if (lanesNode != null)
            {
                SectionId sectionIndex = 0u;
                foreach (var sectionNode in lanesNode.Elements("laneSection"))
                {
                    var s = XmlExt.AsDouble(sectionNode.Attribute("s"));
                    var section = builder.AddRoadSection(road, sectionIndex++, s);

                    AddSideLanes(section, sectionNode.Element("left"), builder);
                    AddSideLanes(section, sectionNode.Element("center"), builder);
                    AddSideLanes(section, sectionNode.Element("right"), builder);
                }
            }
        }
    }

    private static void AddSideLanes(LaneSection section, XElement? side, MapBuilder builder)
    {
        if (side == null) return;
        foreach (var laneNode in side.Elements("lane"))
        {
            var id = XmlExt.AsInt(laneNode.Attribute("id"));
            var typeStr = XmlExt.AsString(laneNode.Attribute("type"));
            var level = XmlExt.AsBool(laneNode.Attribute("level"));

            LaneId predecessor = 0;
            LaneId successor = 0;
            var link = laneNode.Element("link");
            if (link != null)
            {
                var pred = link.Element("predecessor");
                if (pred != null) predecessor = XmlExt.AsInt(pred.Attribute("id"));
                var succ = link.Element("successor");
                if (succ != null) successor = XmlExt.AsInt(succ.Attribute("id"));
            }

            var laneType = (uint)(int)StringToLaneType(typeStr);
            builder.AddRoadSectionLane(section, id, laneType, level, predecessor, successor);
        }
    }

    private static LaneType StringToLaneType(string raw)
    {
        var s = raw.ToLowerInvariant();
        return s switch
        {
            "driving" => LaneType.Driving,
            "stop" => LaneType.Stop,
            "shoulder" => LaneType.Shoulder,
            "biking" => LaneType.Biking,
            "sidewalk" => LaneType.Sidewalk,
            "border" => LaneType.Border,
            "restricted" => LaneType.Restricted,
            "parking" => LaneType.Parking,
            "bidirectional" => LaneType.Bidirectional,
            "median" => LaneType.Median,
            "special1" => LaneType.Special1,
            "special2" => LaneType.Special2,
            "special3" => LaneType.Special3,
            "roadworks" => LaneType.RoadWorks,
            "tram" => LaneType.Tram,
            "rail" => LaneType.Rail,
            "entry" => LaneType.Entry,
            "exit" => LaneType.Exit,
            "offramp" => LaneType.OffRamp,
            "onramp" => LaneType.OnRamp,
            _ => LaneType.None,
        };
    }
}
