// Source: carla/opendrive/parser/ProfilesParser.{h,cpp}
//
// Two profiles per road:
//   - <elevationProfile><elevation> — cubic in s, applied to road height z.
//   - <lateralProfile> — superelevation / crossfall / shape. Upstream parses
//     these into vectors then *does nothing with them* (only the unused TODO
//     branch is commented out). We replicate that: emit lateral profile data
//     only if a hook becomes available later; otherwise no-op for parity.
//
// If a road has NO elevation profile, upstream emits a default zero record.
using System.Xml.Linq;
using CarlaNet.Map.Road;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class ProfilesParser
{
    public static void Parse(XDocument xml, MapBuilder builder)
    {
        var root = xml.Root;
        if (root == null) return;

        foreach (var roadNode in root.Elements("road"))
        {
            var roadId = XmlExt.AsUInt(roadNode.Attribute("id"));
            var road = builder.GetRoad(roadId);

            var profile = roadNode.Element("elevationProfile");
            int count = 0;
            if (profile != null)
            {
                foreach (var elev in profile.Elements("elevation"))
                {
                    builder.AddRoadElevationProfile(road,
                        XmlExt.AsDouble(elev.Attribute("s")),
                        XmlExt.AsDouble(elev.Attribute("a")),
                        XmlExt.AsDouble(elev.Attribute("b")),
                        XmlExt.AsDouble(elev.Attribute("c")),
                        XmlExt.AsDouble(elev.Attribute("d")));
                    count++;
                }
            }
            if (count == 0)
            {
                // Required by upstream: at least one elevation record per road, defaulting to zero.
                builder.AddRoadElevationProfile(road, 0.0, 0.0, 0.0, 0.0, 0.0);
            }

            // <lateralProfile> is parsed but discarded upstream (only TODO comments).
            // We don't read it either — would only add noise.
        }
    }
}
