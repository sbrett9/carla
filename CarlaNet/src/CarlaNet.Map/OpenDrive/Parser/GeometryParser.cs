// Source: carla/opendrive/parser/GeometryParser.{h,cpp}
//
// Reads each <road><planView><geometry> element and dispatches to MapBuilder.
// The five primitives (line / arc / spiral / poly3 / paramPoly3) share common
// (s,x,y,hdg,length) attributes; type-specific attributes come from the single
// child element. We honour upstream's tolerance for missing attributes by using
// XmlAttributeExt's "as_double() defaults to 0" semantics.
using System.Xml.Linq;
using CarlaNet.Map.Road;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class GeometryParser
{
    public static void Parse(XDocument xml, MapBuilder builder)
    {
        var root = xml.Root;
        if (root == null) return;

        foreach (var roadNode in root.Elements("road"))
        {
            var planView = roadNode.Element("planView");
            if (planView == null) continue;

            var roadId = XmlExt.AsUInt(roadNode.Attribute("id"));
            var road = builder.GetRoad(roadId);

            foreach (var geoNode in planView.Elements("geometry"))
            {
                var s = XmlExt.AsDouble(geoNode.Attribute("s"));
                var x = XmlExt.AsDouble(geoNode.Attribute("x"));
                var y = XmlExt.AsDouble(geoNode.Attribute("y"));
                var hdg = XmlExt.AsDouble(geoNode.Attribute("hdg"));
                var length = XmlExt.AsDouble(geoNode.Attribute("length"));

                var type = geoNode.Elements().FirstOrDefault();
                if (type == null) continue;
                var typeName = type.Name.LocalName;

                switch (typeName)
                {
                    case "line":
                        builder.AddRoadGeometryLine(road, s, x, y, hdg, length);
                        break;
                    case "arc":
                        builder.AddRoadGeometryArc(road, s, x, y, hdg, length,
                            XmlExt.AsDouble(type.Attribute("curvature")));
                        break;
                    case "spiral":
                        // SIGN PRESERVED: curvStart and curvEnd carry sign (curving left vs right).
                        builder.AddRoadGeometrySpiral(road, s, x, y, hdg, length,
                            XmlExt.AsDouble(type.Attribute("curvStart")),
                            XmlExt.AsDouble(type.Attribute("curvEnd")));
                        break;
                    case "poly3":
                        builder.AddRoadGeometryPoly3(road, s, x, y, hdg, length,
                            XmlExt.AsDouble(type.Attribute("a")),
                            XmlExt.AsDouble(type.Attribute("b")),
                            XmlExt.AsDouble(type.Attribute("c")),
                            XmlExt.AsDouble(type.Attribute("d")));
                        break;
                    case "paramPoly3":
                        builder.AddRoadGeometryParamPoly3(road, s, x, y, hdg, length,
                            XmlExt.AsDouble(type.Attribute("aU")),
                            XmlExt.AsDouble(type.Attribute("bU")),
                            XmlExt.AsDouble(type.Attribute("cU")),
                            XmlExt.AsDouble(type.Attribute("dU")),
                            XmlExt.AsDouble(type.Attribute("aV")),
                            XmlExt.AsDouble(type.Attribute("bV")),
                            XmlExt.AsDouble(type.Attribute("cV")),
                            XmlExt.AsDouble(type.Attribute("dV")),
                            XmlExt.AsString(type.Attribute("pRange"), "arcLength"));
                        break;
                }
            }
        }
    }
}
