namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The scenario epoch: what simulated second zero means in civil time, and every way a declaration
/// of it can be wrong.
/// </summary>
/// <remarks>
/// Most of these are failure paths, because the epoch's value is in what it refuses. A declaration
/// that parses is a sun somebody will render under, and the likeliest errors -- an offset applied
/// in the wrong direction, a date that does not exist, a civil time with no offset -- all produce a
/// scene that looks entirely ordinary.
/// </remarks>
public sealed class SolarEpochTests
{
    /// <summary>
    /// The sizing scenario's epoch as the scenario contract's worked example writes it: midnight at
    /// the start of day 0 at the port, which every guard shift's trip id already assumes.
    /// </summary>
    private const string PortEpoch = """
        {
          "epoch_version": 1,
          "civil_datetime": "2026-03-21T00:00:00+03:30",
          "utc_offset_hours": 3.5,
          "utc_datetime": "2026-03-20T20:30:00Z",
          "calendar_advances": true,
          "dst_in_effect": false,
          "time_zone_id": "Asia/Tehran",
          "note": "t = 0 is midnight at the start of day 0 at the port, as every guard_dD_hH trip id already assumes."
        }
        """;

    [Fact]
    public void TheContractSWorkedExampleIsReadAsWritten()
    {
        SolarEpoch epoch = SolarEpoch.FromJson(PortEpoch);

        Assert.Equal(new DateTimeOffset(2026, 3, 21, 0, 0, 0, TimeSpan.FromHours(3.5)),
                     epoch.CivilDateTime);
        Assert.Equal(3.5, epoch.UtcOffsetHours);
        Assert.Equal(new DateTimeOffset(2026, 3, 20, 20, 30, 0, TimeSpan.Zero), epoch.UtcDateTime);
        Assert.True(epoch.CalendarAdvances);
        Assert.False(epoch.DstInEffect);
        Assert.Equal("Asia/Tehran", epoch.TimeZoneId);
        Assert.Equal("2026-03-21T00:00:00+03:30", epoch.CivilDateTimeText);
    }

    [Fact]
    public void ASimulatedSecondBecomesTheCivilInstantTheTripIdsAssert()
    {
        SolarEpoch epoch = SolarEpoch.FromJson(PortEpoch);

        // The night shift of day 0, and day 4's morning shift -- the start of the shipped
        // guard-no-show anomaly. Both are the numbers the trip identifiers carry, and both stay at
        // the declared offset rather than drifting to the host's.
        Assert.Equal("2026-03-21T23:00:00+03:30",
                     SolarEpoch.FormatCivil(epoch.CivilInstantAt(82_800)));
        Assert.Equal("2026-03-25T07:00:00+03:30",
                     SolarEpoch.FormatCivil(epoch.CivilInstantAt(370_800)));
        Assert.Equal("2026-03-25T03:30:00Z", SolarEpoch.FormatUtc(epoch.CivilInstantAt(370_800)));

        // A fractional second survives, because a SUMO step shorter than a second is ordinary.
        Assert.Equal("2026-03-21T00:00:00.05+03:30",
                     SolarEpoch.FormatCivil(epoch.CivilInstantAt(0.05)));
    }

    [Fact]
    public void AnEpochDeclaredFromItsPartsIsTheSameEpoch()
    {
        SolarEpoch parsed = SolarEpoch.FromJson(PortEpoch);
        SolarEpoch declared = SolarEpoch.Declare(
            "2026-03-21T00:00:00+03:30", 3.5, "2026-03-20T20:30:00Z", calendarAdvances: true,
            dstInEffect: false, "Asia/Tehran",
            "t = 0 is midnight at the start of day 0 at the port, as every guard_dD_hH trip id already assumes.");

        Assert.Equal(parsed.Digest, declared.Digest);
        Assert.Equal(parsed.CivilDateTime, declared.CivilDateTime);
    }

    [Fact]
    public void TheDigestIsWhatThePythonCompilerComputes()
    {
        // Computed with json.dumps(epoch, sort_keys=True, indent=2) and hashlib.sha256 on the same
        // two objects. The scenario compiler that writes epoch_block_sha256 is Python, so an
        // implementation here that disagreed about one space or one escape would disagree about
        // which epoch a record names.
        Assert.Equal("979f424f6f030bacbdd659afd1adec90c00a91be4ea3df2f39ec0fd7964ab40e",
                     SolarEpoch.FromJson(PortEpoch).Digest);

        // An integer offset stays an integer, and a note needing every kind of escape is escaped
        // the way Python escapes it.
        SolarEpoch awkward = SolarEpoch.FromJson("""
            {"epoch_version": 1, "civil_datetime": "2026-12-21T00:00:00-07:00",
             "utc_offset_hours": -7, "utc_datetime": "2026-12-21T07:00:00Z",
             "calendar_advances": false, "dst_in_effect": false,
             "note": "Arapahoe ± \"quoted\" \\ tab\there"}
            """);
        Assert.Equal("03d09ccbf1037fb1c366580812fbd5eaada4aa293d8f2c18faf799bddea29501",
                     awkward.Digest);
    }

    [Theory]
    [InlineData("2026-03-21T00:00:00+05:45", 5.75, "2026-03-20T18:15:00Z")]
    [InlineData("2026-03-21T00:00:00-03:30", -3.5, "2026-03-21T03:30:00Z")]
    [InlineData("2026-03-21T00:00:00+12:45", 12.75, "2026-03-20T11:15:00Z")]
    [InlineData("2026-03-21T00:00:00Z", 0.0, "2026-03-21T00:00:00+00:00")]
    public void HalfAndQuarterHourOffsetsAreOrdinary(string civil, double offset, string utc)
    {
        SolarEpoch epoch = SolarEpoch.Declare(civil, offset, utc, true, false);

        Assert.Equal(offset, epoch.UtcOffsetHours);
        Assert.Equal(epoch.CivilDateTime.UtcDateTime, epoch.UtcDateTime.UtcDateTime);
    }

    [Fact]
    public void AnOffsetAppliedInTheWrongDirectionIsNamedAsSuch()
    {
        // Local time PLUS the offset, rather than minus it: seven hours from the declared instant
        // at +03:30, in a scene nothing about which looks wrong.
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.Declare("2026-03-21T00:00:00+03:30", 3.5, "2026-03-21T03:30:00Z",
                                     true, false));

        Assert.Contains("applied in the wrong direction", refused.Message);
        Assert.Contains("7 h earlier", refused.Message);
    }

    [Fact]
    public void AUtcInstantThatIsSimplyWrongIsRefusedWithoutGuessingWhy()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.Declare("2026-03-21T00:00:00+03:30", 3.5, "2026-03-20T21:30:00Z",
                                     true, false));

        Assert.Contains("1 h earlier", refused.Message);
        Assert.DoesNotContain("wrong direction", refused.Message);
    }

    [Fact]
    public void ACivilTimeWithNoOffsetIsNotACivilTime()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.Declare("2026-03-21T00:00:00", 3.5, "2026-03-20T20:30:00Z",
                                     true, false));

        Assert.Contains("has no offset", refused.Message);
    }

    [Fact]
    public void TheOffsetInTheCivilTimeAndTheDeclaredOffsetMustAgree()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.Declare("2026-03-21T00:00:00+03:00", 3.5, "2026-03-20T21:00:00Z",
                                     true, false));
        Assert.Contains("must agree", refused.Message);

        CoSimSessionRefusedException zulu = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.Declare("2026-03-21T00:00:00Z", 3.5, "2026-03-21T00:00:00Z",
                                     true, false));
        Assert.Contains("+00:00", zulu.Message);
    }

    [Theory]
    [InlineData("2026-02-31T00:00:00+03:30", "day 31 of a month with 28 days")]
    [InlineData("2024-02-30T00:00:00+03:30", "day 30 of a month with 29 days")]
    [InlineData("2026-13-01T00:00:00+03:30", "month 13")]
    [InlineData("2026-03-21T24:00:00+03:30", "not a time of day")]
    public void ADateTheCalendarDoesNotHaveIsRefusedRatherThanClamped(string civil, string expected)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.Declare(civil, 3.5, "2026-03-20T20:30:00Z", true, false));

        Assert.Contains(expected, refused.Message);
    }

    [Theory]
    [InlineData(3.6, "quarter hours")]
    [InlineData(14.25, "outside -12 to +14")]
    [InlineData(-12.5, "outside -12 to +14")]
    public void AnOffsetNoCivilZoneUsesIsRefused(double offset, string expected)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.Declare("2026-03-21T00:00:00+03:30", offset, "2026-03-20T20:30:00Z",
                                     true, false));

        Assert.Contains(expected, refused.Message);
    }

    [Fact]
    public void EveryRuleABrokenDeclarationBreaksIsNamedAtOnce()
    {
        // No calendar_advances, no dst_in_effect, an unknown field, and an unsupported version. An
        // author told about one at a time fixes them one run at a time.
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.FromJson("""
                {"epoch_version": 2, "civil_datetime": "2026-03-21T00:00:00+03:30",
                 "utc_offset_hours": 3.5, "utc_datetime": "2026-03-20T20:30:00Z",
                 "calender_advances": true}
                """));

        Assert.Contains("epoch_version 2", refused.Message);
        Assert.Contains("'calender_advances' is not an epoch field", refused.Message);
        Assert.Contains("calendar_advances is required", refused.Message);
        Assert.Contains("dst_in_effect is required", refused.Message);
    }

    [Fact]
    public void SomethingThatIsNotAnEpochObjectIsRefused()
    {
        Assert.Throws<CoSimSessionRefusedException>(() => SolarEpoch.FromJson("not json"));
        Assert.Throws<CoSimSessionRefusedException>(() => SolarEpoch.FromJson("[1, 2]"));
        Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.FromJson("""{"civil_datetime": 20260321}"""));
    }
}
