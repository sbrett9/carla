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
    /// The Shahid Bahonar Port scenario's epoch: midnight at
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

    /// <summary>The epoch the shipped Bahonar scenario was compiled against, as its compile lock records it.</summary>
    internal const string BahonarEpoch = """
        {
          "calendar_advances": true,
          "civil_datetime": "2026-09-29T07:00:00+03:30",
          "dst_in_effect": false,
          "epoch_version": 1,
          "note": "Shahid Bahonar Port, Bandar Abbas: simulated second zero is 07:00 local time on 29 September 2026, the first morning shift change; the civil date advances",
          "time_zone_id": "Asia/Tehran",
          "utc_datetime": "2026-09-29T03:30:00Z",
          "utc_offset_hours": 3.5
        }
        """;

    /// <summary>The lock's <c>epoch_block_sha256</c> for <see cref="BahonarEpoch"/>.</summary>
    internal const string BahonarDigest = "f1ba0ddab35f800a4b78ebc902fa9a1dd9d47a64fee72eb3cfab687969bbecdf";

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

    [Fact]
    public void BahonarMovedToAUtcInstantKeepsItsOffsetZoneDaylightSavingAndCalendar()
    {
        SolarEpoch compiled = SolarEpoch.FromJson(BahonarEpoch);
        Assert.Equal(BahonarDigest, compiled.Digest);

        SolarEpoch moved = compiled.MovedTo(SolarEpoch.ReadInstant("2026-10-22T17:40:00Z", "epoch_at"));

        Assert.Equal("2026-10-22T21:10:00+03:30", moved.CivilDateTimeText);
        Assert.Equal("2026-10-22T17:40:00Z", SolarEpoch.FormatUtc(moved.UtcDateTime));
        Assert.Equal((3.5, false, true, "Asia/Tehran"),
                     (moved.UtcOffsetHours, moved.DstInEffect, moved.CalendarAdvances, moved.TimeZoneId));
        // The escort transits' first departure, at 3 d 03:19:29 from step 0, on the moved clock.
        Assert.Equal("2026-10-26T00:29:29+03:30", SolarEpoch.FormatCivil(moved.CivilInstantAt(271_169)));

        // Python's json.dumps(epoch, sort_keys=True, indent=2) of the same object, and its SHA-256.
        Assert.Equal("""
            {
              "calendar_advances": true,
              "civil_datetime": "2026-10-22T21:10:00+03:30",
              "dst_in_effect": false,
              "epoch_version": 1,
              "note": "Simulated second zero moved at run time to 2026-10-22T21:10:00+03:30 from 2026-09-29T07:00:00+03:30, the epoch f1ba0ddab35f800a4b78ebc902fa9a1dd9d47a64fee72eb3cfab687969bbecdf.",
              "time_zone_id": "Asia/Tehran",
              "utc_datetime": "2026-10-22T17:40:00Z",
              "utc_offset_hours": 3.5
            }
            """.ReplaceLineEndings("\n"), moved.CanonicalJson);
        Assert.Equal("59f0d5a8bb8bbe38a03880bc02f902cfefcad4bade22cb722da313c292d5fbd8", moved.Digest);

        SolarEpoch reread = SolarEpoch.FromJson(moved.CanonicalJson);
        Assert.Equal((moved.Digest, moved.CanonicalJson), (reread.Digest, reread.CanonicalJson));
    }

    [Theory]
    [InlineData("2026-10-22T17:40:00")]
    [InlineData("2026-10-22T17:40:00+00:00")]
    [InlineData("2026-10-22T17:40:00.0000000Z")]
    public void AUtcInstantWrittenWithOrWithoutItsDesignatorIsOneInstantAndOneEpoch(string text)
    {
        DateTimeOffset instant = SolarEpoch.ReadInstant(text, "epoch_at");

        Assert.Equal(TimeSpan.Zero, instant.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 17, 40, 0, TimeSpan.Zero), instant);
        Assert.Equal("59f0d5a8bb8bbe38a03880bc02f902cfefcad4bade22cb722da313c292d5fbd8",
                     SolarEpoch.FromJson(BahonarEpoch).MovedTo(instant).Digest);
    }

    [Fact]
    public void AFractionOfASecondSurvivesTheMove()
    {
        SolarEpoch moved = SolarEpoch.FromJson(BahonarEpoch)
            .MovedTo(SolarEpoch.ReadInstant("2026-10-22T17:40:00.25Z", "epoch_at"));

        Assert.Equal("2026-10-22T21:10:00.25+03:30", moved.CivilDateTimeText);
        Assert.Equal("2026-10-22T17:40:00.25Z", SolarEpoch.FormatUtc(moved.UtcDateTime));
    }

    [Fact]
    public void MovingAnEpochToItsOwnInstantLeavesItAsItIs()
    {
        SolarEpoch compiled = SolarEpoch.FromJson(BahonarEpoch);

        SolarEpoch moved = compiled.MovedTo(SolarEpoch.ReadInstant("2026-09-29T03:30:00Z", "epoch_at"));

        Assert.Same(compiled, moved);
        Assert.Equal(BahonarDigest, moved.Digest);
    }

    [Fact]
    public void AnInstantWithAnOffsetOtherThanUtcIsRefusedNamingItInUtc()
    {
        // The site's own offset, and one that is no site's: each the same kind of mistake.
        CoSimSessionRefusedException local = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.ReadInstant("2026-10-22T21:10:00+03:30", "--epoch-at"));
        Assert.Equal("Simulated second zero is not moved: --epoch-at '2026-10-22T21:10:00+03:30' carries the "
                     + "offset +03:30, and the instant is given in UTC: write it 2026-10-22T17:40:00Z.",
                     local.Message);

        CoSimSessionRefusedException elsewhere = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.ReadInstant("2026-10-22T13:40:00-04:00", "epoch_at"));
        Assert.Contains("carries the offset -04:00", elsewhere.Message);
        Assert.Contains("write it 2026-10-22T17:40:00Z", elsewhere.Message);

        // Handed as a value, the same instant at +03:30 is refused by the move itself.
        CoSimSessionRefusedException handed = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.FromJson(BahonarEpoch).MovedTo(
                new DateTimeOffset(2026, 10, 22, 21, 10, 0, TimeSpan.FromHours(3.5))));
        Assert.Contains("2026-10-22T21:10:00+03:30 carries the offset +03:30", handed.Message);
        Assert.Contains("the epoch's own offset, UTC+03:30. Give it as 2026-10-22T17:40:00Z.", handed.Message);
    }

    [Theory]
    [InlineData("2026-10-22", "is not a UTC date and time of the form 2026-10-22T17:40:00Z")]
    [InlineData("tomorrow", "is not a UTC date and time of the form 2026-10-22T17:40:00Z")]
    [InlineData("2026-10-22 17:40:00Z", "is not a UTC date and time")]
    [InlineData("2026-02-29T17:40:00Z", "names day 29 of a month with 28 days")]
    [InlineData("2026-10-22T24:00:00Z", "is not a time of day")]
    public void AnInstantTheGrammarOrTheCalendarDoesNotHaveIsRefused(string text, string expected)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SolarEpoch.ReadInstant(text, "epoch_at"));

        Assert.StartsWith($"Simulated second zero is not moved: epoch_at '{text}' ", refused.Message);
        Assert.Contains(expected, refused.Message);
    }

    [Fact]
    public void AnOffsetWrittenAsAnIntegerStaysAnIntegerThroughTheMove()
    {
        // Declare would write -7 as -7.0, and Python digests the two differently.
        SolarEpoch compiled = SolarEpoch.FromJson("""
            {"epoch_version": 1, "civil_datetime": "2026-12-21T00:00:00-07:00",
             "utc_offset_hours": -7, "utc_datetime": "2026-12-21T07:00:00Z",
             "calendar_advances": false, "dst_in_effect": false}
            """);
        Assert.Equal("0ad3f1925267521547f5ff5a890967dbf6abc1bfc79a536da365d2b94060c451", compiled.Digest);

        SolarEpoch moved = compiled.MovedTo(SolarEpoch.ReadInstant("2026-12-22T14:00:00Z", "epoch_at"));

        Assert.Contains("\"utc_offset_hours\": -7\n", moved.CanonicalJson);
        Assert.Contains("\"calendar_advances\": false", moved.CanonicalJson);
        Assert.Equal("2026-12-22T07:00:00-07:00", moved.CivilDateTimeText);
        Assert.Equal(SolarEpoch.FromJson(moved.CanonicalJson).Digest, moved.Digest);
    }
}
