// Source: carla/opendrive/parser/JunctionParser.{h,cpp}
//
// Parses <junction> elements: their connections (incoming road → connecting
// road) with per-lane links (from → to), plus the set of <controller>s that
// govern the junction.
using System.Collections.Generic;
using System.Xml.Linq;
using CarlaNet.Map.Road;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class JunctionParser
{
    public static void Parse(XDocument xml, MapBuilder builder)
    {
        var root = xml.Root;
        if (root == null) return;

        foreach (var jNode in root.Elements("junction"))
        {
            var id = XmlExt.AsInt(jNode.Attribute("id"));
            var name = XmlExt.AsString(jNode.Attribute("name"));
            builder.AddJunction(id, name);

            foreach (var connNode in jNode.Elements("connection"))
            {
                var connId = XmlExt.AsUInt(connNode.Attribute("id"));
                var incoming = XmlExt.AsUInt(connNode.Attribute("incomingRoad"));
                var connecting = XmlExt.AsUInt(connNode.Attribute("connectingRoad"));
                builder.AddConnection(id, connId, incoming, connecting);

                foreach (var llNode in connNode.Elements("laneLink"))
                {
                    var from = XmlExt.AsInt(llNode.Attribute("from"));
                    var to = XmlExt.AsInt(llNode.Attribute("to"));
                    builder.AddLaneLink(id, connId, from, to);
                }
            }

            // controllers
            var controllerIds = new List<ContId>();
            foreach (var cNode in jNode.Elements("controller"))
            {
                controllerIds.Add(XmlExt.AsString(cNode.Attribute("id")));
            }
            if (controllerIds.Count > 0)
            {
                builder.AddJunctionController(id, controllerIds);
            }
        }
    }
}
