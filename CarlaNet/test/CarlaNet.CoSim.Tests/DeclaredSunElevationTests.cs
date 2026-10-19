using CarlaNet.Types.Illumination;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A declared elevation and an illumination band's edges mean one elevation of the sun.
/// </summary>
/// <remarks>
/// The bands' edges are elevations <c>11_Time_And_Illumination.md</c> §4.4 declares, so they mean
/// what every declared elevation means (open question 7). The ruling is one constant here and one in
/// the band table, which the recorder reads without this assembly; were one reversed alone, a capture's
/// band would be cut from one elevation while its window was declared by the other.
/// </remarks>
public class DeclaredSunElevationTests
{
    [Fact]
    public void The_Bands_Are_Cut_From_The_Elevation_A_Declaration_Means()
    {
        Assert.Equal(DeclaredSunElevation.Kind, IlluminationBands.Elevation);
        Assert.Equal(DeclaredSunElevation.Name, SolarElevationKinds.Name(IlluminationBands.Elevation));
    }

    [Fact]
    public void A_Declared_Elevation_Is_The_Refraction_Corrected_One()
    {
        var sun = new SunPosition(ElevationDegrees: -0.25, CorrectedElevationDegrees: 0.31, AzimuthDegrees: 95.4);

        Assert.Equal("refraction_corrected", DeclaredSunElevation.Name);
        Assert.Equal(0.31, DeclaredSunElevation.Of(sun));
        Assert.Equal(IlluminationBand.Golden, IlluminationBands.Of(DeclaredSunElevation.Of(sun)));
    }
}
