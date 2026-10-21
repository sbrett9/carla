namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The live track's options read from the JSON a caller hands the session, each member optional and
/// defaulted, and refused whole, naming every rule broken, where any value is unusable.
/// </summary>
public sealed class LiveTrackOptionsTests
{
    [Fact]
    public void AnEmptyObjectTakesEveryDefault()
    {
        LiveTrackOptions options = LiveTrackOptions.FromJson("{}");

        Assert.Equal(1.0, options.RateHz);
        Assert.Equal(3.0, options.StaleSeconds);
        Assert.Equal("CARLA-TRUTH", options.UidPrefix);
        Assert.Equal("n", options.Affiliation);
        Assert.Empty(options.AffiliationByPopulation);
        Assert.Equal("n", options.AffiliationOf("mil_truck"));
    }

    [Fact]
    public void EveryMemberIsReadAsGiven()
    {
        LiveTrackOptions options = LiveTrackOptions.FromJson("""
            {"rate_hz": 2.5, "stale_s": 7, "uid_prefix": "RUN-7", "affiliation": "u",
             "affiliation_by_type": {"mil_truck": "f", "guard": "f", "civ_car": "n"}}
            """);

        Assert.Equal(2.5, options.RateHz);
        Assert.Equal(7.0, options.StaleSeconds);
        Assert.Equal("RUN-7", options.UidPrefix);
        Assert.Equal("u", options.Affiliation);
        Assert.Equal("f", options.AffiliationOf("guard"));
        Assert.Equal("n", options.AffiliationOf("civ_car"));
        Assert.Equal("u", options.AffiliationOf("unnamed"));
        Assert.Equal(3, options.AffiliationByPopulation.Count);
    }

    [Theory]
    [InlineData("not json", "are not JSON")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("""{"rate": 1}""", "'rate' is not a live track option")]
    [InlineData("""{"rate_hz": 0}""", "rate_hz 0 must be a positive number")]
    [InlineData("""{"rate_hz": -1}""", "rate_hz -1 must be a positive number")]
    [InlineData("""{"rate_hz": "1"}""", "rate_hz must be a number")]
    [InlineData("""{"stale_s": 0}""", "stale_s 0 must be a positive number")]
    [InlineData("""{"uid_prefix": ""}""", "uid_prefix '' must be one or more characters")]
    [InlineData("""{"uid_prefix": "CARLA TRUTH"}""", "none of them whitespace")]
    [InlineData("""{"uid_prefix": 7}""", "uid_prefix must be a string")]
    [InlineData("""{"affiliation": "z"}""", "affiliation 'z' must be one of the letters puafnshjkox")]
    [InlineData("""{"affiliation": "nf"}""", "affiliation 'nf' must be one of the letters")]
    [InlineData("""{"affiliation_by_type": ["f"]}""", "affiliation_by_type must be an object")]
    [InlineData("""{"affiliation_by_type": {"guard": "hostile"}}""", "gives 'guard' 'hostile'")]
    [InlineData("""{"affiliation_by_type": {"guard": 1}}""", "gives 'guard' Number")]
    [InlineData("""{"affiliation_by_type": {"": "f"}}""", "names an empty population")]
    public void AnUnusableValueIsRefusedInItsOwnWords(string json, string reason)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => LiveTrackOptions.FromJson(json));
        Assert.Contains(reason, refused.Message);
    }

    [Fact]
    public void EveryRuleBrokenIsNamedAtOnce()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => LiveTrackOptions.FromJson("""{"rate_hz": 0, "stale_s": -3, "affiliation": "q", "extra": true}"""));

        Assert.StartsWith("The live track's options are refused. (1) 'extra' is not a live track option", refused.Message);
        Assert.Contains("(2) rate_hz 0", refused.Message);
        Assert.Contains("(3) stale_s -3", refused.Message);
        Assert.Contains("(4) affiliation 'q'", refused.Message);
    }

    [Fact]
    public void OptionsBuiltInCodeAreHeldToTheSameRules()
    {
        Assert.Throws<CoSimSessionRefusedException>(() => new LiveTrackOptions(rateHz: double.NaN));
        Assert.Throws<CoSimSessionRefusedException>(() => new LiveTrackOptions(staleSeconds: double.PositiveInfinity));
        Assert.Throws<CoSimSessionRefusedException>(() => new LiveTrackOptions(uidPrefix: "A\tB"));
        Assert.Throws<CoSimSessionRefusedException>(
            () => new LiveTrackOptions(affiliationByPopulation: new Dictionary<string, string> { ["guard"] = "F" }));
    }
}
