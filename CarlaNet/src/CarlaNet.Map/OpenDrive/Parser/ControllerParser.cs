// Source: carla/opendrive/parser/ControllerParser.{h,cpp}
//
// <OpenDRIVE><controller id=".."> with child <control signalId=".."> elements
// describe a group of signals (typically the heads of one traffic-light at a
// junction). Stored verbatim into MapBuilder; the post-build pass wires the
// controllers to their junctions.
using System.Collections.Generic;
using System.Xml.Linq;
using CarlaNet.Map.Road;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class ControllerParser
{
    public static void Parse(XDocument xml, MapBuilder builder)
    {
        var root = xml.Root;
        if (root == null) return;

        foreach (var cNode in root.Elements("controller"))
        {
            var id = XmlExt.AsString(cNode.Attribute("id"));
            var name = XmlExt.AsString(cNode.Attribute("name"));
            var seq = XmlExt.AsUInt(cNode.Attribute("sequence"));

            var signals = new List<SignId>();
            foreach (var ctrl in cNode.Elements("control"))
            {
                signals.Add(XmlExt.AsString(ctrl.Attribute("signalId")));
            }

            builder.CreateController(id, name, seq, signals);
        }
    }
}
