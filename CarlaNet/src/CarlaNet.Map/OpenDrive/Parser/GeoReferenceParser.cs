// Source: carla/opendrive/parser/GeoReferenceParser.{h,cpp}
//
// Parses the <header><geoReference> PROJ string of the OpenDRIVE document into
// a GeoLocation. Only +lat_0 / +lon_0 from the PROJ string are extracted (PROJ
// strings look like "+proj=tmerc +lat_0=42.0 +lon_0=2.0 +k=1 +x_0=0 +y_0=0").
using System;
using System.Xml.Linq;
using CarlaNet.Map.Road;
using CarlaNet.Types.Geom;

namespace CarlaNet.Map.OpenDrive.Parser;

internal static class GeoReferenceParser
{
    public static void Parse(XDocument xml, MapBuilder builder)
    {
        var root = xml.Root;
        if (root == null || root.Name.LocalName != "OpenDRIVE")
        {
            builder.SetGeoReference(DefaultGeoLocation());
            return;
        }

        var header = root.Element("header");
        var geoRef = header?.Element("geoReference")?.Value ?? string.Empty;
        builder.SetGeoReference(ParseGeoReference(geoRef));
    }

    private static GeoLocation ParseGeoReference(string s)
    {
        double lat = double.NaN;
        double lon = double.NaN;

        foreach (var element in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = element.IndexOf('=');
            if (eq <= 0) continue;
            var key = element.Substring(0, eq);
            var val = element.Substring(eq + 1);
            if (key == "+lat_0" && double.TryParse(val, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pl))
            {
                lat = pl;
            }
            else if (key == "+lon_0" && double.TryParse(val, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pn))
            {
                lon = pn;
            }
        }

        if (double.IsNaN(lat) || double.IsNaN(lon))
        {
            // Upstream default per parser source.
            return new GeoLocation(42.0, 2.0, 0.0);
        }
        return new GeoLocation(lat, lon, 0.0);
    }

    private static GeoLocation DefaultGeoLocation() => new(42.0, 2.0, 0.0);
}
