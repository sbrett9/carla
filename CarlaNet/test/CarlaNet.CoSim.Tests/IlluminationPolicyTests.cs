namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The illumination policy as a scenario declares it, and every declaration that asks for two
/// things that cannot both be true.
/// </summary>
/// <remarks>
/// Each refusal here is an operator who would otherwise have got something other than what they
/// wrote -- a rate beside a freeze is a moving sun asked for and a still one delivered -- with
/// nothing in the run saying so.
/// </remarks>
public sealed class IlluminationPolicyTests
{
    [Fact]
    public void TheContractSWorkedExampleIsReadAsWritten()
    {
        IlluminationPolicy policy = IlluminationPolicy.FromJson("""
            {
              "illumination_version": 1,
              "policy": "freeze_at_window_start",
              "freeze_date_advances": false,
              "require_sun": true,
              "note": "Illumination held constant within each window so the six windows differ only in hour."
            }
            """);

        Assert.Equal(IlluminationPolicyKind.FreezeAtWindowStart, policy.Kind);
        Assert.False(policy.Advances);
        Assert.Equal(0.0, policy.Rate);
        Assert.True(policy.RequireSun);
        Assert.True(policy.HonoursTheEpoch);
        Assert.Equal("freeze_at_window_start, date held", policy.ToString());
    }

    [Fact]
    public void EachPolicyReadsItsOwnParameters()
    {
        IlluminationPolicy advance = IlluminationPolicy.FromJson(
            """{"illumination_version": 1, "policy": "advance", "rate_sun_s_per_sim_s": 1.0}""");
        Assert.True(advance.Advances);
        Assert.Equal(1.0, advance.Rate);

        IlluminationPolicy freezeAt = IlluminationPolicy.FromJson(
            """{"illumination_version": 1, "policy": "freeze_at", "freeze_at_civil_time": "15:00:00"}""");
        Assert.Equal(TimeSpan.FromHours(15), freezeAt.FreezeAtCivilTimeOfDay);
        Assert.False(freezeAt.HonoursTheEpoch);

        IlluminationPolicy ignore = IlluminationPolicy.FromJson(
            """{"illumination_version": 1, "policy": "ignore", "require_sun": false}""");
        Assert.False(ignore.BindsTheSun);
        Assert.False(ignore.RequireSun);

        // A sun is required unless the declaration says otherwise, under every policy.
        Assert.True(IlluminationPolicy.FromJson(
            """{"illumination_version": 1, "policy": "ignore"}""").RequireSun);
    }

    [Theory]
    [InlineData("""{"illumination_version": 1}""", "policy is required")]
    [InlineData("""{"illumination_version": 1, "policy": "frozen"}""", "policy 'frozen' is not one of")]
    [InlineData("""{"illumination_version": 1, "policy": "advance"}""", "needs rate_sun_s_per_sim_s")]
    [InlineData("""{"illumination_version": 1, "policy": "advance", "rate_sun_s_per_sim_s": 0}""",
                "must be positive")]
    [InlineData("""{"illumination_version": 1, "policy": "freeze_at_window_start", "rate_sun_s_per_sim_s": 3600}""",
                "believe the sun was moving")]
    [InlineData("""{"illumination_version": 1, "policy": "freeze_at"}""", "needs freeze_at_civil_time")]
    [InlineData("""{"illumination_version": 1, "policy": "freeze_at", "freeze_at_civil_time": "15:00"}""",
                "written HH:MM:SS")]
    [InlineData("""{"illumination_version": 1, "policy": "freeze_at", "freeze_at_civil_time": "24:00:00"}""",
                "not a time of day")]
    [InlineData("""{"illumination_version": 1, "policy": "advance", "rate_sun_s_per_sim_s": 1, "freeze_at_civil_time": "07:00:00"}""",
                "belongs to the freeze_at policy")]
    [InlineData("""{"illumination_version": 1, "policy": "advance", "rate_sun_s_per_sim_s": 1, "freeze_date_advances": true}""",
                "belongs to a freeze")]
    [InlineData("""{"illumination_version": 1, "policy": "freeze_at_window_start", "solar_audit_tolerance_elev_deg": 5}""",
                "off switch")]
    [InlineData("""{"illumination_version": 1, "policy": "freeze_at_window_start", "sun": "noon"}""",
                "'sun' is not an illumination field")]
    [InlineData("""{"illumination_version": 2, "policy": "freeze_at_window_start"}""",
                "illumination_version 2")]
    public void ADeclarationThatAsksForTwoIncompatibleThingsIsRefused(string json, string expected)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => IlluminationPolicy.FromJson(json));

        Assert.Contains(expected, refused.Message);
    }

    [Fact]
    public void AMissingPolicyIsNotGivenTheRecommendedOne()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => IlluminationPolicy.FromJson("""{"illumination_version": 1}"""));

        Assert.Contains("There is no default", refused.Message);
        Assert.Contains("freeze_at_window_start is the recommended one", refused.Message);
    }

    [Fact]
    public void ThePoliciesBuiltInCodeRefuseWhatTheParserRefuses()
    {
        Assert.Throws<CoSimSessionRefusedException>(() => IlluminationPolicy.Advance(0.0));
        Assert.Throws<CoSimSessionRefusedException>(() => IlluminationPolicy.Advance(double.NaN));
        Assert.Throws<CoSimSessionRefusedException>(
            () => IlluminationPolicy.FreezeAt(TimeSpan.FromHours(24)));
    }
}
