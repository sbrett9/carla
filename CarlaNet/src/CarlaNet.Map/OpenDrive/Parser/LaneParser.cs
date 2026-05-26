// Source: carla/opendrive/parser/LaneParser.{h,cpp}
//
// For each <road>/<lanes>/<laneSection>/<left|center|right>/<lane>:
//   - emits per-lane width records (or a default 0-width if missing for a
//     non-center lane, mirroring upstream's warning),
//   - emits border / mark / mark-line / material / visibility / speed /
//     access / height / rule records.
// Lanes themselves are already created by RoadParser; we look them up via
// MapBuilder.GetLane(road_id, lane_id, s).
using System.Xml.Linq;
using CarlaNet.Map.Road;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class LaneParser
{
    public static void Parse(XDocument xml, MapBuilder builder)
    {
        var root = xml.Root;
        if (root == null) return;

        foreach (var roadNode in root.Elements("road"))
        {
            var roadId = XmlExt.AsUInt(roadNode.Attribute("id"));

            foreach (var lanesNode in roadNode.Elements("lanes"))
            {
                foreach (var sectionNode in lanesNode.Elements("laneSection"))
                {
                    var s = XmlExt.AsDouble(sectionNode.Attribute("s"));

                    ParseLanes(roadId, s, sectionNode.Element("left"), builder);
                    ParseLanes(roadId, s, sectionNode.Element("center"), builder);
                    ParseLanes(roadId, s, sectionNode.Element("right"), builder);
                }
            }
        }
    }

    private static void ParseLanes(RoadId roadId, double s, XElement? parent, MapBuilder builder)
    {
        if (parent == null) return;
        foreach (var laneNode in parent.Elements("lane"))
        {
            var laneId = XmlExt.AsInt(laneNode.Attribute("id"));
            var lane = builder.GetLane(roadId, laneId, s);

            // Lane Width
            int widthCount = 0;
            foreach (var w in laneNode.Elements("width"))
            {
                var sOff = XmlExt.AsDouble(w.Attribute("sOffset"));
                builder.CreateLaneWidth(lane, sOff + s,
                    XmlExt.AsDouble(w.Attribute("a")),
                    XmlExt.AsDouble(w.Attribute("b")),
                    XmlExt.AsDouble(w.Attribute("c")),
                    XmlExt.AsDouble(w.Attribute("d")));
                widthCount++;
            }
            if (widthCount == 0)
            {
                // Default zero-width per upstream (suppresses warning for center lane).
                builder.CreateLaneWidth(lane, s, 0.0, 0.0, 0.0, 0.0);
            }

            // Lane Border
            foreach (var b in laneNode.Elements("border"))
            {
                var sOff = XmlExt.AsDouble(b.Attribute("sOffset"));
                builder.CreateLaneBorder(lane, sOff + s,
                    XmlExt.AsDouble(b.Attribute("a")),
                    XmlExt.AsDouble(b.Attribute("b")),
                    XmlExt.AsDouble(b.Attribute("c")),
                    XmlExt.AsDouble(b.Attribute("d")));
            }

            // Lane Road Mark
            int markId = 0;
            foreach (var rm in laneNode.Elements("roadMark"))
            {
                var sOff = XmlExt.AsDouble(rm.Attribute("sOffset"));
                var typeS = XmlExt.AsString(rm.Attribute("type"));
                var weight = XmlExt.AsString(rm.Attribute("weight"));
                var color = XmlExt.AsString(rm.Attribute("color"));
                var material = XmlExt.AsString(rm.Attribute("material"));
                var width = XmlExt.AsDouble(rm.Attribute("width"));
                var laneChange = XmlExt.AsString(rm.Attribute("laneChange"));
                var height = XmlExt.AsDouble(rm.Attribute("height"));

                string typeName = string.Empty;
                double typeWidth = 0.0;
                var markType = rm.Element("type");
                if (markType != null)
                {
                    typeName = XmlExt.AsString(markType.Attribute("name"));
                    typeWidth = XmlExt.AsDouble(markType.Attribute("width"));
                }

                bool isRht = lane.Section?.Road?.IsRightHandTraffic ?? true;
                builder.CreateRoadMark(lane, markId, sOff + s,
                    typeS, weight, color, material, width, laneChange, height, typeName, typeWidth, isRht);

                if (markType != null)
                {
                    foreach (var line in markType.Elements("line"))
                    {
                        var lineLen = XmlExt.AsDouble(line.Attribute("length"));
                        var space = XmlExt.AsDouble(line.Attribute("space"));
                        var t = XmlExt.AsDouble(line.Attribute("tOffset"));
                        var lineSOff = XmlExt.AsDouble(line.Attribute("sOffset"));
                        var rule = XmlExt.AsString(line.Attribute("rule"));
                        var lineWidth = XmlExt.AsDouble(line.Attribute("width"));
                        builder.CreateRoadMarkTypeLine(lane, markId, lineLen, space, t, lineSOff + s, rule, lineWidth);
                    }
                }
                markId++;
            }

            // Material
            foreach (var m in laneNode.Elements("material"))
            {
                builder.CreateLaneMaterial(lane,
                    XmlExt.AsDouble(m.Attribute("sOffset")) + s,
                    XmlExt.AsString(m.Attribute("surface")),
                    XmlExt.AsDouble(m.Attribute("friction")),
                    XmlExt.AsDouble(m.Attribute("roughness")));
            }

            // Visibility
            foreach (var v in laneNode.Elements("visibility"))
            {
                builder.CreateLaneVisibility(lane,
                    XmlExt.AsDouble(v.Attribute("sOffset")) + s,
                    XmlExt.AsDouble(v.Attribute("forward")),
                    XmlExt.AsDouble(v.Attribute("back")),
                    XmlExt.AsDouble(v.Attribute("left")),
                    XmlExt.AsDouble(v.Attribute("right")));
            }

            // Speed
            foreach (var sp in laneNode.Elements("speed"))
            {
                builder.CreateLaneSpeed(lane,
                    XmlExt.AsDouble(sp.Attribute("sOffset")) + s,
                    XmlExt.AsDouble(sp.Attribute("max")),
                    XmlExt.AsString(sp.Attribute("unit")));
            }

            // Access
            foreach (var a in laneNode.Elements("access"))
            {
                builder.CreateLaneAccess(lane,
                    XmlExt.AsDouble(a.Attribute("sOffset")) + s,
                    XmlExt.AsString(a.Attribute("restriction")));
            }

            // Height
            foreach (var h in laneNode.Elements("height"))
            {
                builder.CreateLaneHeight(lane,
                    XmlExt.AsDouble(h.Attribute("sOffset")) + s,
                    XmlExt.AsDouble(h.Attribute("inner")),
                    XmlExt.AsDouble(h.Attribute("outer")));
            }

            // Rule
            foreach (var r in laneNode.Elements("rule"))
            {
                builder.CreateLaneRule(lane,
                    XmlExt.AsDouble(r.Attribute("sOffset")) + s,
                    XmlExt.AsString(r.Attribute("value")));
            }
        }
    }
}
