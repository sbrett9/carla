// Source: carla/opendrive/parser/SignalParser.{h,cpp}
//
// Parses each <road>/<signals>/<signal> and <signalReference> into MapBuilder.
// Signals also carry <validity> (lane-id ranges) and <dependency> (cross-references)
// and may use <positionInertial> for world-coords positions instead of s/t.
using System.Xml.Linq;
using CarlaNet.Map.Road;
using CarlaNet.Map.Road.Element;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class SignalParser
{
    public static void Parse(XDocument xml, MapBuilder builder)
    {
        var root = xml.Root;
        if (root == null) return;

        foreach (var roadNode in root.Elements("road"))
        {
            var roadId = XmlExt.AsUInt(roadNode.Attribute("id"));
            var signalsNode = roadNode.Element("signals");
            if (signalsNode == null) continue;

            var road = builder.GetRoad(roadId);

            foreach (var sigNode in signalsNode.Elements("signal"))
            {
                var sigRef = builder.AddSignal(
                    road,
                    XmlExt.AsString(sigNode.Attribute("id")),
                    XmlExt.AsDouble(sigNode.Attribute("s")),
                    XmlExt.AsDouble(sigNode.Attribute("t")),
                    XmlExt.AsString(sigNode.Attribute("name")),
                    XmlExt.AsString(sigNode.Attribute("dynamic")),
                    XmlExt.AsString(sigNode.Attribute("orientation")),
                    XmlExt.AsDouble(sigNode.Attribute("zOffset")),
                    XmlExt.AsString(sigNode.Attribute("country")),
                    XmlExt.AsString(sigNode.Attribute("type")),
                    XmlExt.AsString(sigNode.Attribute("subtype")),
                    XmlExt.AsDouble(sigNode.Attribute("value")),
                    XmlExt.AsString(sigNode.Attribute("unit")),
                    XmlExt.AsDouble(sigNode.Attribute("height")),
                    XmlExt.AsDouble(sigNode.Attribute("width")),
                    XmlExt.AsString(sigNode.Attribute("text")),
                    XmlExt.AsDouble(sigNode.Attribute("hOffset")),
                    XmlExt.AsDouble(sigNode.Attribute("pitch")),
                    XmlExt.AsDouble(sigNode.Attribute("roll")));

                AddValidity(sigRef, sigNode, builder);

                foreach (var dep in sigNode.Elements("dependency"))
                {
                    builder.AddDependencyToSignal(
                        XmlExt.AsString(sigNode.Attribute("id")),
                        XmlExt.AsString(dep.Attribute("id")),
                        XmlExt.AsString(dep.Attribute("type")));
                }

                foreach (var pos in sigNode.Elements("positionInertial"))
                {
                    builder.AddSignalPositionInertial(
                        XmlExt.AsString(sigNode.Attribute("id")),
                        XmlExt.AsDouble(pos.Attribute("x")),
                        XmlExt.AsDouble(pos.Attribute("y")),
                        XmlExt.AsDouble(pos.Attribute("z")),
                        XmlExt.AsDouble(pos.Attribute("hdg")),
                        XmlExt.AsDouble(pos.Attribute("pitch")),
                        XmlExt.AsDouble(pos.Attribute("roll")));
                }
            }

            foreach (var refNode in signalsNode.Elements("signalReference"))
            {
                var sigRef = builder.AddSignalReference(
                    road,
                    XmlExt.AsString(refNode.Attribute("id")),
                    XmlExt.AsDouble(refNode.Attribute("s")),
                    XmlExt.AsDouble(refNode.Attribute("t")),
                    XmlExt.AsString(refNode.Attribute("orientation")));
                AddValidity(sigRef, refNode, builder);
            }
        }
    }

    private static void AddValidity(RoadInfoSignal sigRef, XElement parent, MapBuilder builder)
    {
        foreach (var v in parent.Elements("validity"))
        {
            builder.AddValidityToSignalReference(sigRef,
                XmlExt.AsInt(v.Attribute("fromLane")),
                XmlExt.AsInt(v.Attribute("toLane")));
        }
    }
}
