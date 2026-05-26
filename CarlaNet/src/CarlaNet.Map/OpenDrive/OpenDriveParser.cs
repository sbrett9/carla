// Source: carla/opendrive/OpenDriveParser.{h,cpp}
//
// Top-level entry: takes the raw .xodr string, sequences the per-element
// parsers in the same order as upstream, returns the assembled Map (or null
// on parse failure).
using System;
using System.Xml.Linq;
using CarlaNet.Map.OpenDrive.Parser;
using CarlaNet.Map.Road;

namespace CarlaNet.Map.OpenDrive;

public static class OpenDriveParser
{
    /// <summary>
    /// Load an OpenDRIVE XML string into a fully-resolved Map. Returns null if the
    /// XML fails to parse. Mirrors upstream's std::optional&lt;road::Map&gt;.
    /// </summary>
    public static Road.Map? Load(string openDriveXml)
    {
        XDocument xml;
        try
        {
            xml = XDocument.Parse(openDriveXml);
        }
        catch (Exception)
        {
            // Upstream logs "unable to parse the OpenDRIVE XML string" and returns empty.
            return null;
        }

        var builder = new MapBuilder();

        GeoReferenceParser.Parse(xml, builder);
        RoadParser.Parse(xml, builder);
        JunctionParser.Parse(xml, builder);
        GeometryParser.Parse(xml, builder);
        LaneParser.Parse(xml, builder);
        ProfilesParser.Parse(xml, builder);
        TrafficGroupParser.Parse(xml, builder);
        SignalParser.Parse(xml, builder);
        ObjectParser.Parse(xml, builder);
        ControllerParser.Parse(xml, builder);

        return builder.Build();
    }
}
