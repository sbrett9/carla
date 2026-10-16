namespace CarlaNet.CoSim.Tests;

/// <summary>
/// A session refuses a scenario whose configuration lets SUMO edit its own population in a way the truth
/// record cannot carry -- a collision action that moves or removes vehicles, a teleport trigger besides
/// <c>time-to-teleport</c> unless the run accepts it, a random offset on the departures, a seed from the
/// wall clock -- and names the demand scale and the insertion limits it runs under.
/// </summary>
/// <remarks>
/// The thresholds are the ones measured against SUMO 1.27.0 on the fixture network. Under a
/// <c>time-to-teleport</c> of -1: a vehicle held on a lane that does not continue its route, on the
/// 27.78 m/s approach, stood for the whole 1000 s run with <c>time-to-teleport.highways</c> absent, 0 or
/// -1, and was teleported after 5 s with 5; a vehicle on a disconnected route stood for the whole run
/// with <c>time-to-teleport.disconnected</c> absent or -1, and was teleported after one step of waiting
/// with 0 and after 5 s with 5; a vehicle type setting <c>timeToTeleport</c> 5 was teleported after 5 s
/// of being blocked. <c>random-depart-offset</c> 5 moved all four of the fixture's departures, and -5 none.
/// An unnamed collision action (<c>Warn</c>) was an error on SUMO's console and the run went on;
/// <c>random</c>, <c>ignore-accidents</c>, <c>scale</c> and <c>max-num-vehicles</c> set to something
/// SUMO cannot read were each an error and the run went on under the default.
/// </remarks>
public sealed class SumoDistributionEditCheckTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "carlanet-distribution-" + Guid.NewGuid().ToString("n"));

    public SumoDistributionEditCheckTests()
    {
        Directory.CreateDirectory(_directory);
    }

    // -- The collision action -------------------------------------------------------------------------

    [Theory]
    [InlineData("teleport", "under which SUMO moves the collider to the next edge of its route, a jump the "
                            + "interpolation cannot connect")]
    [InlineData("remove", "under which SUMO takes both vehicles out, and they leave the population as though "
                          + "they had arrived")]
    [InlineData("none", "under which SUMO skips the collision check, so the run could not say whether any "
                        + "collision happened")]
    [InlineData("Warn", "which is not one of SUMO's four actions -- none, warn, teleport, remove -- and under "
                        + "which SUMO writes an error and runs on, moving every collider as teleport does")]
    public void ACollisionActionOtherThanWarnIsRefusedNamingIt(string action, string why)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(Configuration(string.Empty, collision: action),
                                                    allowTeleporting: false));

        Assert.Contains($"(1) collision.action is '{action}', {why}; a run may carry only warn, under which SUMO "
                        + "registers each collision and changes nothing about the traffic, as the scenario "
                        + "compiler writes", refused.Message);
        Assert.Contains("SUMO has not been started", refused.Message);
        Assert.Equal(CoSimSessionStage.Validation, refused.Stage);
    }

    [Fact]
    public void ACollisionActionLeftToSumoSDefaultIsRefused()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(Configuration(string.Empty, collision: null),
                                                    allowTeleporting: false));

        Assert.Contains("(1) it sets no collision.action, and SUMO's default is 'teleport', under which SUMO "
                        + "moves the collider", refused.Message);
    }

    [Fact]
    public void AcceptingTeleportingAcceptsNoCollisionActionThatTeleports()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(Configuration(string.Empty, collision: "teleport"),
                                                    allowTeleporting: true));

        Assert.Contains("collision.action is 'teleport'", refused.Message);
        Assert.DoesNotContain("--allow-teleporting", refused.Message);
    }

    [Theory]
    [InlineData("<collision.action value=\"warn\"/>")]
    [InlineData("<collision.action v=\"warn\"/>")]
    [InlineData("<collision.action>warn</collision.action>")]
    public void WarnRunsAndTheActionInForceIsRecorded(string option)
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(
            Configuration(option, collision: null), allowTeleporting: false);

        Assert.Equal("warn", check.CollisionActionDeclared);
        Assert.Equal("warn", check.CollisionActionInForce);
        Assert.Equal("'warn' (collision.action 'warn'); a run may carry only warn", check.CollisionText);
        Assert.StartsWith("collision.action 'warn'; ", check.ToString());
    }

    [Fact]
    public void TheOnlyActionARunMayCarryIsWarn()
    {
        Assert.Equal(new[] { "warn" }, SumoDistributionEditCheck.PermittedCollisionActions);
    }

    [Theory]
    [InlineData("<ignore-accidents value=\"true\"/>", "true")]
    [InlineData("<collision.action value=\"warn\"/><ignore-accidents value=\"1\"/>", "1")]
    public void IgnoringAccidentsIsRefusedAsNoneIsWhateverTheAction(string options, string value)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(Configuration(options, collision: null), allowTeleporting: false));

        Assert.Contains($"(1) ignore-accidents is '{value}', under which SUMO skips the collision check whatever "
                        + "the action, so the run could not say whether any collision happened; a run may carry "
                        + "only warn", refused.Message);
    }

    [Fact]
    public void IgnoringNoAccidentsRunsUnderTheAction()
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(
            Configuration("<ignore-accidents value=\"false\"/>"), allowTeleporting: false);

        Assert.False(check.AccidentsIgnored);
        Assert.Equal("warn", check.CollisionActionInForce);
    }

    // -- The teleport triggers time-to-teleport leaves open -------------------------------------------

    [Theory]
    [InlineData("time-to-teleport.highways", "5", "a vehicle waiting on a lane that does not continue its "
                                                 + "route, on a fast road, once it has waited 5 s")]
    [InlineData("time-to-teleport.highways", "00:00:00.5", "a vehicle waiting on a lane that does not "
                                                          + "continue its route, on a fast road, once it "
                                                          + "has waited 0.5 s")]
    [InlineData("time-to-teleport.bidi", "60", "a vehicle waiting on a bidirectional edge once it has waited 60 s")]
    [InlineData("time-to-teleport.railsignal-deadlock", "00:01:00",
                "a vehicle held in a rail-signal deadlock once it has waited 60 s")]
    [InlineData("time-to-teleport.disconnected", "5", "a vehicle whose edge has no lane onto the next edge of "
                                                     + "its route once it has waited 5 s")]
    public void ATeleportTriggerThatIsOnIsRefusedNamingWhatItTeleports(string option, string value, string what)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(Configuration($"<{option} value=\"{value}\"/>"),
                                                    allowTeleporting: false));

        Assert.Contains($"(1) {option} is '{value}', so SUMO teleports {what}", refused.Message);
        Assert.Contains("--allow-teleporting", refused.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.0004")] // which rounds to zero milliseconds, as SUMO rounds a time
    public void ADisconnectedRouteIsTeleportedFromZeroUp(string value)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(
                Configuration($"<time-to-teleport.disconnected value=\"{value}\"/>"), allowTeleporting: false));

        Assert.Contains($"time-to-teleport.disconnected is '{value}', so SUMO teleports a vehicle whose edge has "
                        + "no lane onto the next edge of its route as soon as it waits (zero enables it; -1 "
                        + "disables it)", refused.Message);
    }

    [Theory]
    [InlineData("time-to-teleport.highways", "0")]
    [InlineData("time-to-teleport.highways", "-1")]
    [InlineData("time-to-teleport.bidi", "0")]
    [InlineData("time-to-teleport.bidi", "-1")]
    [InlineData("time-to-teleport.railsignal-deadlock", "0")]
    [InlineData("time-to-teleport.disconnected", "-1")]
    [InlineData("time-to-teleport.disconnected", "-0.001")]
    public void ATeleportTriggerSetOffRunsAndIsRecorded(string option, string value)
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(
            Configuration($"<{option} value=\"{value}\"/>"), allowTeleporting: false);

        SumoTeleportTrigger trigger = Assert.Single(check.TeleportTriggers, each => each.Name == option);
        Assert.Equal(value, trigger.Declared);
        Assert.False(trigger.Enabled);
        Assert.False(check.TeleportingEnabled);
        Assert.Contains($"{option} '{value}'", check.TeleportText);
        Assert.StartsWith("off: ", check.TeleportText);
    }

    [Fact]
    public void EveryTeleportTriggerAtSumoSDefaultIsOffAndRecordedAsTheDefault()
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(
            Configuration(string.Empty), allowTeleporting: false);

        Assert.False(check.TeleportingEnabled);
        Assert.False(check.TeleportingAccepted);
        Assert.Equal("off: time-to-teleport.highways not set, so SUMO's default of 0 s; "
                     + "time-to-teleport.disconnected not set, so SUMO's default of -1 s; "
                     + "time-to-teleport.bidi not set, so SUMO's default of -1 s; "
                     + "time-to-teleport.railsignal-deadlock not set, so SUMO's default of -1 s; "
                     + "no vehicle type sets its own", check.TeleportText);
    }

    [Theory]
    [InlineData("timeToTeleport", "a vehicle of that type that is blocked once it has waited 5 s, whatever "
                                  + "time-to-teleport says")]
    [InlineData("timeToTeleportBidi", "a vehicle of that type waiting on a bidirectional edge once it has "
                                      + "waited 5 s, whatever time-to-teleport.bidi says")]
    public void AVehicleTypeThatTeleportsItsOwnIsRefusedNamingTheTypeAndFile(string attribute, string what)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(
                Configuration(string.Empty, routeTypes: $"<vType id=\"impatient\" {attribute}=\"5\"/>"),
                allowTeleporting: false));

        Assert.Contains($"(1) vehicle type 'impatient' in routes.rou.xml sets {attribute} '5', so SUMO "
                        + $"teleports {what}", refused.Message);
        Assert.Contains("--allow-teleporting", refused.Message);
    }

    [Fact]
    public void AVehicleTypeInAnAdditionalFileOrADistributionIsReadToo()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(
                Configuration(string.Empty,
                              additionalTypes: "<vTypeDistribution id=\"mix\"><vType id=\"late\" "
                                               + "timeToTeleport=\"00:01:00\"/></vTypeDistribution>"),
                allowTeleporting: false));

        Assert.Contains("vehicle type 'late' in more.add.xml sets timeToTeleport '00:01:00', so SUMO teleports "
                        + "a vehicle of that type that is blocked once it has waited 60 s", refused.Message);
    }

    [Fact]
    public void AVehicleTypeThatTurnsItsOwnOffRunsAndIsRecorded()
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(
            Configuration(string.Empty, routeTypes: "<vType id=\"patient\" timeToTeleport=\"-1\"/>"),
            allowTeleporting: false);

        SumoTeleportTrigger trigger = check.TeleportTriggers[^1];
        Assert.Equal("vType 'patient' timeToTeleport", trigger.Name);
        Assert.False(trigger.Enabled);
        Assert.EndsWith("; vType 'patient' timeToTeleport '-1'", check.TeleportText);
    }

    [Fact]
    public void TeleportTriggersAcceptedExplicitlyRunAndAreRecordedAsEnabled()
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(
            Configuration("<time-to-teleport.highways value=\"5\"/>",
                          routeTypes: "<vType id=\"impatient\" timeToTeleport=\"30\"/>"),
            allowTeleporting: true);

        Assert.True(check.TeleportingEnabled);
        Assert.True(check.TeleportingAccepted);
        Assert.StartsWith("ENABLED, accepted explicitly: time-to-teleport.highways '5', ENABLED after 5 s; ",
                          check.TeleportText);
        Assert.EndsWith("; vType 'impatient' timeToTeleport '30', ENABLED after 30 s", check.TeleportText);
        Assert.Contains("a teleport trigger ENABLED, accepted explicitly", check.ToString());

        // Accepting teleporting where nothing enables it accepts nothing.
        Assert.False(SumoDistributionEditCheck.Require(Configuration(string.Empty), allowTeleporting: true)
                         .TeleportingAccepted);
    }

    // -- Departures and seeding -----------------------------------------------------------------------

    [Theory]
    [InlineData("5", "5")]
    [InlineData("0.5", "0.5")]
    [InlineData("00:01:00", "60")]
    public void ARandomOffsetOnTheDeparturesIsRefused(string value, string seconds)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(
                Configuration($"<random-depart-offset value=\"{value}\"/>"), allowTeleporting: true));

        Assert.Contains($"(1) random-depart-offset is '{value}', so SUMO moves every departure by a random "
                        + $"offset of up to {seconds} s, and no departure the truth declares is one a vehicle "
                        + "departed at; remove it", refused.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void ARandomOffsetOfZeroOrLessMovesNoDepartureAndRuns(string value)
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(
            Configuration($"<random-depart-offset value=\"{value}\"/>"), allowTeleporting: false);

        Assert.Equal(value, check.RandomDepartOffsetDeclared);
        Assert.Equal($"none: no departure moved by a random offset (random-depart-offset '{value}')",
                     check.DepartOffsetText);
    }

    [Theory]
    [InlineData("<random value=\"true\"/>", "random", "true")]
    [InlineData("<random value=\"1\"/>", "random", "1")]
    [InlineData("<random v=\"On\"/>", "random", "On")]
    [InlineData("<abs-rand value=\"yes\"/>", "random", "yes")]
    public void AnUnseededRunIsRefused(string option, string name, string value)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(Configuration(option), allowTeleporting: false));

        Assert.Contains($"(1) {name} is '{value}', so SUMO seeds itself from the wall clock in place of the "
                        + "seed, the run's traffic cannot be run again", refused.Message);
    }

    [Fact]
    public void ARunSeededFromItsSeedRunsAndSaysSo()
    {
        Assert.Equal("from the seed, so the traffic can be run again (random 'false')",
                     SumoDistributionEditCheck.Require(Configuration("<random value=\"false\"/>"), false).SeedingText);
        Assert.Equal("from the seed, so the traffic can be run again (random not set)",
                     SumoDistributionEditCheck.Require(Configuration(string.Empty), false).SeedingText);
    }

    // -- Recorded: the demand scale and the insertion limits ------------------------------------------

    [Theory]
    [InlineData("<scale value=\"0.5\"/>", 0.5,
                "SCALED by 0.5: SUMO discards or duplicates vehicles to match (scale '0.5')")]
    [InlineData("<scale value=\"2\"/>", 2.0,
                "SCALED by 2: SUMO discards or duplicates vehicles to match (scale '2')")]
    [InlineData("<scale value=\"1\"/>", 1.0, "1, the demand as written (scale '1')")]
    [InlineData("", 1.0, "1, the demand as written (scale not set, so SUMO's default of 1)")]
    public void TheDemandScaleIsRecordedNotRefused(string option, double scale, string said)
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(Configuration(option), false);

        Assert.Equal(scale, check.Scale);
        Assert.Equal($"{said}; no vehicle type scales its own", check.ScaleText);
    }

    [Fact]
    public void AVehicleTypeSScaleIsRecorded()
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(
            Configuration(string.Empty, routeTypes: "<vType id=\"bus\" scale=\"3\"/><vType id=\"car\"/>"), false);

        Assert.Equal(new[] { ("bus", "3") }, check.TypeScales);
        Assert.EndsWith("; vehicle types SCALED by their own: vType 'bus' scale '3'", check.ScaleText);
        Assert.Contains("demand as written and per vehicle type", check.ToString());
    }

    [Theory]
    [InlineData("<max-num-vehicles value=\"200\"/>", 200,
                "CAPPED at 200 running: SUMO delays an insertion that would exceed it (max-num-vehicles '200')")]
    [InlineData("<max-num-vehicles value=\"-1\"/>", -1, "none (max-num-vehicles '-1')")]
    [InlineData("", -1, "none (max-num-vehicles not set, so SUMO's default of -1)")]
    public void TheCapOnVehiclesRunningIsRecordedNotRefused(string option, int cap, string said)
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(Configuration(option), false);

        Assert.Equal(cap, check.MaxNumVehicles);
        Assert.Equal(said, check.VehicleLimitText);
    }

    [Theory]
    [InlineData("<max-depart-delay value=\"900\"/>", true,
                "a vehicle not inserted within 900 s of its departure is DISCARDED, and recorded as not "
                + "inserted (max-depart-delay '900')")]
    [InlineData("<max-depart-delay value=\"0\"/>", true,
                "a vehicle not inserted within 0 s of its departure is DISCARDED, and recorded as not "
                + "inserted (max-depart-delay '0')")]
    [InlineData("<max-depart-delay value=\"-1\"/>", false,
                "never: a vehicle waits as long as it must to be inserted (max-depart-delay '-1')")]
    [InlineData("", false, "never: a vehicle waits as long as it must to be inserted (max-depart-delay not "
                           + "set, so SUMO's default of -1 s)")]
    public void WhenAVehicleNotInsertedIsDiscardedIsRecordedNotRefused(string option, bool discards, string said)
    {
        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(Configuration(option), false);

        Assert.Equal(discards, check.DiscardsLateInsertions);
        Assert.Equal(said, check.DepartDelayText);
    }

    // -- What SUMO would not read ---------------------------------------------------------------------

    [Theory]
    [InlineData("<time-to-teleport.highways value=\"soon\"/>", "time-to-teleport.highways is 'soon', which is "
                                                               + "not a time SUMO reads")]
    [InlineData("<max-depart-delay value=\"soon\"/>", "max-depart-delay is 'soon', which is not a time SUMO reads")]
    [InlineData("<random-depart-offset value=\"soon\"/>", "random-depart-offset is 'soon', which is not a time")]
    [InlineData("<random value=\"maybe\"/>", "random is 'maybe', which SUMO does not read as true or false: "
                                             + "SUMO writes an error and runs on under its default of false")]
    [InlineData("<ignore-accidents value=\"sometimes\"/>", "ignore-accidents is 'sometimes', which SUMO does not "
                                                           + "read as true or false")]
    [InlineData("<scale value=\"lots\"/>", "scale is 'lots', which is not a number: SUMO writes an error and "
                                           + "runs on under its default of 1")]
    [InlineData("<scale value=\"-1\"/>", "scale is '-1', and SUMO refuses a negative scale too")]
    [InlineData("<max-num-vehicles value=\"2.5\"/>", "max-num-vehicles is '2.5', which is not a whole number: "
                                                     + "SUMO writes an error and runs on under its default of -1")]
    public void AValueSumoWouldNotReadIsRefused(string option, string said)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(Configuration(option), allowTeleporting: false));

        Assert.Contains("(1) " + said, refused.Message);
    }

    [Theory]
    [InlineData("<vType id=\"odd\" timeToTeleport=\"soon\"/>",
                "vehicle type 'odd' in routes.rou.xml sets timeToTeleport to 'soon', which is not a time SUMO reads")]
    [InlineData("<vType id=\"odd\" scale=\"-2\"/>",
                "vehicle type 'odd' in routes.rou.xml sets scale to '-2', which SUMO refuses")]
    public void AVehicleTypeAttributeSumoWouldNotReadIsRefused(string type, string said)
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(Configuration(string.Empty, routeTypes: type), false));

        Assert.Contains("(1) " + said, refused.Message);
    }

    [Fact]
    public void AnOptionSetTwiceIsRefusedAndNothingIsGuessedAboutIt()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(
                Configuration("<collision.action value=\"warn\"/><random value=\"false\"/><abs-rand value=\"false\"/>"),
                allowTeleporting: false));

        Assert.Contains("(1) it sets collision.action 2 times ('warn', 'warn'), and SUMO refuses an option set "
                        + "twice; (2) it sets random 2 times ('false', 'false')", refused.Message);
        Assert.DoesNotContain("SUMO's default is 'teleport'", refused.Message);
    }

    [Fact]
    public void EveryEditRefusedIsNamedInOneRefusal()
    {
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(
                Configuration("<random-depart-offset value=\"5\"/><random value=\"true\"/>"
                              + "<time-to-teleport.bidi value=\"5\"/>", collision: "remove"),
                allowTeleporting: false));

        Assert.Contains("(1) collision.action is 'remove'", refused.Message);
        Assert.Contains("(2) time-to-teleport.bidi is '5'", refused.Message);
        Assert.Contains("(3) random-depart-offset is '5'", refused.Message);
        Assert.Contains("(4) random is 'true'", refused.Message);
    }

    [Fact]
    public void ARouteFileThatIsNotXmlIsRefused()
    {
        string scenario = Configuration(string.Empty);
        File.WriteAllText(Path.Combine(_directory, "routes.rou.xml"), "<routes><vType id=\"cut\"");

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(scenario, false));

        Assert.Contains("routes.rou.xml, which cannot be read as XML, so whether a vehicle type it defines "
                        + "teleports or scales its own vehicles cannot be established", refused.Message);
    }

    [Fact]
    public void ARouteFileThatIsNotThereIsLeftToSumoWhichRefusesItAsItLoads()
    {
        string scenario = Configuration(string.Empty);
        File.Delete(Path.Combine(_directory, "routes.rou.xml"));

        SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(scenario, false);

        Assert.Equal(4, check.TeleportTriggers.Count);
    }

    [Fact]
    public void ACompressedRouteFileIsReadAsSumoReadsIt()
    {
        string scenario = Configuration(string.Empty);
        string compressed = Path.Combine(_directory, "routes.rou.xml.gz");
        using (var gzip = new System.IO.Compression.GZipStream(File.Create(compressed),
                                                                System.IO.Compression.CompressionLevel.Fastest))
        using (var writer = new StreamWriter(gzip))
        {
            writer.Write("<routes><vType id=\"packed\" timeToTeleport=\"5\"/></routes>");
        }

        File.WriteAllText(scenario, File.ReadAllText(scenario).Replace("routes.rou.xml", "routes.rou.xml.gz",
                                                                       StringComparison.Ordinal));

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => SumoDistributionEditCheck.Require(scenario, false));
        Assert.Contains("vehicle type 'packed' in routes.rou.xml.gz sets timeToTeleport '5'", refused.Message);
    }

    // -- What ships -----------------------------------------------------------------------------------

    [Fact]
    public void TheShippedScenariosPassAndTheReportNamesWhatEachRunsUnder()
    {
        foreach (string name in new[]
                 {
                     "Arapahoe_I25_UnderpassDwell.sumocfg",
                     "Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg",
                     "Shahid_Bahonar_Port_PatternOfLife.sumocfg",
                 })
        {
            SumoDistributionEditCheck check = SumoDistributionEditCheck.Require(
                ScenarioLockCheckTests.RepositoryFile("Import", name), allowTeleporting: false);

            Assert.Equal("warn", check.CollisionActionInForce);
            Assert.False(check.TeleportingEnabled);
            Assert.Equal(4, check.TeleportTriggers.Count);
            Assert.Null(check.RandomDepartOffsetDeclared);
            Assert.Null(check.RandomDeclared);
            Assert.Equal(1.0, check.Scale);
            Assert.Empty(check.TypeScales);
            Assert.Equal(-1, check.MaxNumVehicles);
            Assert.Equal("900", check.MaxDepartDelayDeclared);
            Assert.True(check.DiscardsLateInsertions);
            Assert.Equal("collision.action 'warn'; no teleport trigger enabled; departures as declared, from "
                         + "the seed; demand as written; no vehicle limit; a vehicle not inserted within 900 s "
                         + "discarded", check.ToString());
        }
    }

    [Fact]
    public void TheFixturesTheSessionTestsRunPass()
    {
        foreach (string scenario in new[]
                 {
                     CoSimFixtures.RightAngleTurnScenario,
                     CoSimFixtures.SuccessionScenario,
                     CoSimFixtures.DwellScenario,
                     CoSimFixtures.JamScenario,
                 })
        {
            Assert.Equal("warn", SumoDistributionEditCheck.Require(scenario, allowTeleporting: false)
                                     .CollisionActionInForce);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still open somewhere is not worth failing a test over; the temporary directory
            // is the operating system's to clean up.
        }
    }

    /// <summary>
    /// A configuration whose processing section holds the given options -- and, unless
    /// <paramref name="collision"/> is null, the collision action -- beside a route file and an additional
    /// file holding the given vehicle types; its full path.
    /// </summary>
    private string Configuration(string processing, string? collision = "warn", string routeTypes = "",
                                 string additionalTypes = "")
    {
        File.WriteAllText(Path.Combine(_directory, "routes.rou.xml"), $"<routes>{routeTypes}</routes>\n");
        File.WriteAllText(Path.Combine(_directory, "more.add.xml"), $"<additional>{additionalTypes}</additional>\n");
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("n") + ".sumocfg");
        File.WriteAllText(path,
                          "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<configuration>\n    <input>\n"
                          + "        <net-file value=\"RightAngleTurn.net.xml\"/>\n"
                          + "        <route-files value=\"routes.rou.xml\"/>\n"
                          + "        <additional-files value=\"more.add.xml\"/>\n    </input>\n"
                          + "    <processing>"
                          + (collision is null ? string.Empty : $"<collision.action value=\"{collision}\"/>")
                          + $"{processing}</processing>\n</configuration>\n");
        return path;
    }
}
