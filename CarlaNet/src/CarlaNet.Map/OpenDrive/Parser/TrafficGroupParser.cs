// Source: carla/opendrive/parser/TrafficGroupParser.{h,cpp}
//
// Upstream is a no-op: the <userData><trafficGroup ...> block exists in some
// maps but the parser body is entirely commented out (no AddTrafficGroup hook
// on MapBuilder upstream either). We mirror the no-op explicitly so future
// callers can attach behaviour without changing the pipeline.
using System.Xml.Linq;
using CarlaNet.Map.Road;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class TrafficGroupParser
{
    public static void Parse(XDocument xml, MapBuilder builder)
    {
        // Intentionally no-op. Mirrors upstream's all-commented body.
    }
}
