// Source: carla/opendrive/parser/ObjectParser.{h,cpp}
//
// <objects><object> — three flavours upstream cares about:
//   - type="crosswalk" with <outline><cornerLocal u=.. v=.. z=..>
//   - name starting with "Speed_" / "speed_" — RoadRunner-emitted speed
//     signals expressed as objects (type 274)
//   - name containing "Stencil_STOP" — stop-sign stencils (type 206)
// Other object types are ignored (props/walls aren't used by TM).
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using CarlaNet.Map.Road;
using CarlaNet.Map.Road.Element;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class ObjectParser
{
    public static void Parse(XDocument xml, MapBuilder builder)
    {
        var root = xml.Root;
        if (root == null) return;

        foreach (var roadNode in root.Elements("road"))
        {
            var objectsNode = roadNode.Element("objects");
            if (objectsNode == null) continue;

            var roadId = XmlExt.AsUInt(roadNode.Attribute("id"));
            var road = builder.GetRoad(roadId);

            foreach (var objNode in objectsNode.Elements("object"))
            {
                var type = XmlExt.AsString(objNode.Attribute("type"));
                var name = XmlExt.AsString(objNode.Attribute("name"));

                if (type == "crosswalk")
                {
                    var points = new List<CrosswalkPoint>();
                    var outline = objNode.Element("outline");
                    if (outline != null)
                    {
                        foreach (var corner in outline.Elements("cornerLocal"))
                        {
                            points.Add(new CrosswalkPoint(
                                XmlExt.AsDouble(corner.Attribute("u")),
                                XmlExt.AsDouble(corner.Attribute("v")),
                                XmlExt.AsDouble(corner.Attribute("z"))));
                        }
                    }
                    builder.AddRoadObjectCrosswalk(road,
                        name,
                        XmlExt.AsDouble(objNode.Attribute("s")),
                        XmlExt.AsDouble(objNode.Attribute("t")),
                        XmlExt.AsDouble(objNode.Attribute("zOffset")),
                        XmlExt.AsDouble(objNode.Attribute("hdg")),
                        XmlExt.AsDouble(objNode.Attribute("pitch")),
                        XmlExt.AsDouble(objNode.Attribute("roll")),
                        XmlExt.AsString(objNode.Attribute("orientation")),
                        XmlExt.AsDouble(objNode.Attribute("width")),
                        XmlExt.AsDouble(objNode.Attribute("length")),
                        points);
                }
                else if (name.Length >= 6 && (name.StartsWith("Speed_") || name.StartsWith("speed_")))
                {
                    // RoadRunner emits speed limits as objects. Parse the speed value off the name.
                    string speedStr = name.Contains("STATIC") ? name[13..] : name[6..];
                    if (!double.TryParse(speedStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                        speed = 0.0;

                    builder.AddSignal(road,
                        XmlExt.AsString(objNode.Attribute("id")),
                        XmlExt.AsDouble(objNode.Attribute("s")),
                        XmlExt.AsDouble(objNode.Attribute("t")),
                        name,
                        "no",
                        XmlExt.AsString(objNode.Attribute("orientation")),
                        XmlExt.AsDouble(objNode.Attribute("zOffset")),
                        "OpenDRIVE",
                        SignalType.MaximumSpeed,
                        speedStr,
                        speed,
                        "mph",
                        XmlExt.AsDouble(objNode.Attribute("height")),
                        XmlExt.AsDouble(objNode.Attribute("width")),
                        speedStr,
                        XmlExt.AsDouble(objNode.Attribute("hdg")),
                        XmlExt.AsDouble(objNode.Attribute("pitch")),
                        XmlExt.AsDouble(objNode.Attribute("roll")));
                }
                else if (name.Contains("Stencil_STOP"))
                {
                    builder.AddSignal(road,
                        XmlExt.AsString(objNode.Attribute("id")),
                        XmlExt.AsDouble(objNode.Attribute("s")),
                        XmlExt.AsDouble(objNode.Attribute("t")),
                        name,
                        "no",
                        XmlExt.AsString(objNode.Attribute("orientation")),
                        XmlExt.AsDouble(objNode.Attribute("zOffset")),
                        "OpenDRIVE",
                        SignalType.StopSign,
                        string.Empty,
                        0.0,
                        "mph",
                        XmlExt.AsDouble(objNode.Attribute("height")),
                        XmlExt.AsDouble(objNode.Attribute("width")),
                        string.Empty,
                        XmlExt.AsDouble(objNode.Attribute("hdg")),
                        XmlExt.AsDouble(objNode.Attribute("pitch")),
                        XmlExt.AsDouble(objNode.Attribute("roll")));
                }
            }
        }
    }
}
