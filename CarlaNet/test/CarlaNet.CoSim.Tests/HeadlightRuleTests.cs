using CarlaNet.Types.Rpc.Lighting;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Headlights come on below one elevation of sun and go off above a higher one, so a sun sitting near
/// either never switches them frame by frame.
/// </summary>
public sealed class HeadlightRuleTests
{
    private const VehicleLightStateFlags On = VehicleLightStateFlags.Position | VehicleLightStateFlags.LowBeam;

    [Theory]
    [InlineData(-30.0, true)]
    [InlineData(2.999, true)]
    [InlineData(3.0, false)]
    [InlineData(4.5, false)]
    [InlineData(6.5, false)]
    [InlineData(60.0, false)]
    public void TheFirstElevationDecidesAndInsideTheBandTheLampsStartOff(double elevation, bool on)
    {
        var rule = new HeadlightRule(3.0, 6.0);

        Assert.Equal(on ? On : VehicleLightStateFlags.None, rule.Update(elevation));
        Assert.Equal(on, rule.OnAtStart);
        Assert.Equal(0, rule.Switches);
    }

    [Fact]
    public void ASettingSunSwitchesThemOnBelowTheLowerThresholdAndNotBefore()
    {
        var rule = new HeadlightRule(3.0, 6.0);

        Assert.Equal(VehicleLightStateFlags.None, rule.Update(7.0));
        Assert.Equal(VehicleLightStateFlags.None, rule.Update(5.0));
        Assert.Equal(VehicleLightStateFlags.None, rule.Update(3.0));
        Assert.Equal(On, rule.Update(2.99));
        Assert.Equal(On, rule.Update(-5.0));
        Assert.Equal(1, rule.Switches);
    }

    [Fact]
    public void ARisingSunKeepsThemOnThroughTheBandAndSwitchesThemOffAboveIt()
    {
        var rule = new HeadlightRule(3.0, 6.0);

        Assert.Equal(On, rule.Update(-2.0));
        Assert.Equal(On, rule.Update(3.5));
        Assert.Equal(On, rule.Update(6.0));
        Assert.Equal(VehicleLightStateFlags.None, rule.Update(6.01));
        Assert.Equal(1, rule.Switches);
    }

    [Fact]
    public void ASunWanderingInsideTheBandNeverSwitchesThem()
    {
        var rule = new HeadlightRule(3.0, 6.0);
        rule.Update(2.0);
        foreach (double elevation in new[] { 3.1, 5.9, 3.01, 5.99, 4.0 })
        {
            Assert.Equal(On, rule.Update(elevation));
        }

        Assert.Equal(0, rule.Switches);
        Assert.Equal("headlights on below 3 deg of sun, off above 6 deg; on at the first frame, 0 switch(es) since",
                     rule.ToString());
    }

    [Theory]
    [InlineData(6.0, 3.0)]
    [InlineData(3.0, 3.0)]
    [InlineData(double.NaN, 6.0)]
    [InlineData(3.0, double.PositiveInfinity)]
    public void AnEmptyOrInvertedBandIsRefused(double onBelow, double offAbove)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => new HeadlightRule(onBelow, offAbove));

        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
        Assert.Contains("has to be a number above the first", refused.Message);
    }
}
