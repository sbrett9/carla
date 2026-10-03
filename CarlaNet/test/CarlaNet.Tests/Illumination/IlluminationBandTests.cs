// The illumination bands of Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/11_Time_And_Illumination.md
// §4.4, pinned at the edges that section names. This table is the only definition in the tree: the
// recorder writes each capture's band from it, and CarlaControl's statistic and vocabulary read it
// through carlanet (CarlaControl/test/test_illumination_label_association.py pins the same edges there).
using CarlaNet.Types.Illumination;

namespace CarlaNet.Tests.Illumination;

public class IlluminationBandTests
{
    [Fact]
    public void The_Six_Bands_Are_Doc_11_s_From_The_Highest_Sun_Down()
    {
        Assert.Equal(["day", "golden", "civil_twilight", "nautical_twilight", "astronomical_twilight", "night"],
                     IlluminationBands.Names);
        double?[] edges = [6.0, 0.0, -6.0, -12.0, -18.0, null];
        Assert.Equal(edges, IlluminationBands.Table.Select(edge => edge.AboveDegrees));
        Assert.Equal("11_Time_And_Illumination.md §4.4", IlluminationBands.Source);
        // The enumeration runs in the table's order, and each band names itself as the table does.
        Assert.Equal(Enum.GetValues<IlluminationBand>(), IlluminationBands.Table.Select(edge => edge.Band));
        Assert.All(IlluminationBands.Table, edge => Assert.Equal(edge.Name, IlluminationBands.Name(edge.Band)));
    }

    [Theory]
    [InlineData(90.0, "day")]
    [InlineData(6.0001, "day")]
    [InlineData(6.0, "golden")]
    [InlineData(0.0001, "golden")]
    [InlineData(0.0, "civil_twilight")]
    [InlineData(-6.0, "nautical_twilight")]
    [InlineData(-12.0, "astronomical_twilight")]
    [InlineData(-17.9999, "astronomical_twilight")]
    [InlineData(-18.0, "night")]
    [InlineData(-60.95, "night")]
    [InlineData(-90.0, "night")]
    public void Each_Band_Holds_Its_Upper_Edge(double elevation, string band)
    {
        Assert.Equal(band, IlluminationBands.NameOf(elevation));
        Assert.True(IlluminationBands.TryOf(elevation, out IlluminationBand found));
        Assert.Equal(band, IlluminationBands.Name(found));
    }

    [Theory]
    [InlineData(-180.0)]
    [InlineData(90.0001)]
    [InlineData(-90.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void An_Elevation_That_Is_Not_A_Sun_s_Has_No_Band_Rather_Than_Night(double elevation)
    {
        // -180 is what the engine reports for a sun it could not compute (doc 11 F4): below -18, and
        // not a night anybody rendered.
        Assert.False(IlluminationBands.TryOf(elevation, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => IlluminationBands.Of(elevation));
    }

    [Theory]
    [InlineData(5.4, 6.0, "golden", SolarElevationKind.RefractionCorrected)]
    [InlineData(5.4, 6.0001, "day", SolarElevationKind.RefractionCorrected)]
    [InlineData(-0.6, 0.0, "civil_twilight", SolarElevationKind.RefractionCorrected)]
    [InlineData(6.0, null, "golden", SolarElevationKind.Geometric)]
    [InlineData(6.0001, null, "day", SolarElevationKind.Geometric)]
    public void A_Reported_Sun_Is_Cut_From_Its_Corrected_Elevation_And_From_Its_Geometric_One_Only_Without(
        double geometric, double? corrected, string band, SolarElevationKind cutFrom)
    {
        // The rule every record that writes a band beside a reported sun follows: a capture's solar block
        // and the world truth track.
        Assert.True(IlluminationBands.TryOfReported(geometric, corrected, out IlluminationBand found,
                                                    out SolarElevationKind from));
        Assert.Equal((band, cutFrom), (IlluminationBands.Name(found), from));
    }

    [Fact]
    public void A_Reported_Sun_The_Engine_Could_Not_Compute_Has_No_Band()
    {
        Assert.False(IlluminationBands.TryOfReported(-180.0, -180.0, out _, out _));
        Assert.False(IlluminationBands.TryOfReported(-180.0, null, out _, out _));
    }

    [Fact]
    public void The_Bands_Are_Stated_Against_The_Elevation_The_Light_Is_Rotated_By()
    {
        Assert.Equal(SolarElevationKind.RefractionCorrected, IlluminationBands.Elevation);
        Assert.Equal("refraction_corrected", SolarElevationKinds.Name(IlluminationBands.Elevation));
        Assert.Equal("geometric", SolarElevationKinds.Name(SolarElevationKind.Geometric));
    }
}
