using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CarlaNet.Map.WorldPackage;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Whether a world package describes the world a server has loaded: every way it can fail to, and
/// the one way it can succeed.
/// </summary>
/// <remarks>
/// The loaded world is what a server holding a given package's world answers
/// (<see cref="SyntheticWorld.Describe"/>), changed in exactly one respect per test, so each refusal
/// is attributable to the one thing that differs. The grids are compared by digest, so a grid that
/// differs is a server answering another grid's digest, and the digest it answers is computed here
/// from a grid changed by as little as one bit. The last tests point the check at the real packages
/// in <c>Build/world-packages</c>: one real world against another, and each against itself.
/// </remarks>
public sealed class LoadedWorldCheckTests
{
    [Fact]
    public void APackageDescribesTheWorldItWasWrittenFrom()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            at => 0.01 * at.X, CoSimFixtures.RightAngleTurnNetwork, "!");

        Assert.Empty(LoadedWorldCheck.Disagreements(world.PackagePath, world.AsLoaded()));
        LoadedWorldCheck.Require(world.PackagePath, world.AsLoaded());
    }

    [Fact]
    public void AWorldThatCarriesNoRecordIsRefused()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        LoadedWorld stock = world.AsLoaded() with { BareEarth = null };

        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => LoadedWorldCheck.Require(world.PackagePath, stock));
        Assert.Contains("carries no bare-earth reference record", refused.Message);
        Assert.Single(LoadedWorldCheck.Disagreements(world.PackagePath, stock));
    }

    [Fact]
    public void AnotherBuildOfTheSameAreaIsRefusedOnTheGroundItIsSeatedOn()
    {
        // The same origin, the same grid and the same road network; the surface of the eastern half
        // stands a metre higher, as a rebuild over re-streamed terrain would.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");
        using SyntheticWorld rebuilt = SyntheticWorld.Write(
            at => at.X > 0.0 ? 1.0 : 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        string disagreement = Assert.Single(
            LoadedWorldCheck.Disagreements(world.PackagePath, rebuilt.AsLoaded()));

        // The offset grids are both zero everywhere, so only the ground is named, by both digests.
        Assert.Equal($"the loaded world's bare-earth ground grid has SHA-1 {GroundSha1(rebuilt)} and "
                     + $"the package's has SHA-1 {GroundSha1(world)}", disagreement);
        Assert.NotEqual(GroundSha1(rebuilt), GroundSha1(world));
    }

    [Fact]
    public void OneBitOfEitherGridDifferingIsRefusedNamingBothDigests()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            at => 0.01 * at.X, CoSimFixtures.RightAngleTurnNetwork, "!");

        LoadedWorld loaded = world.AsLoaded();
        Assert.True(WorldPackage.TryReadGrids(world.PackagePath, out float[] offset, out float[] ground));
        string offsetSha1 = WorldPackage.HashGrid(offset);
        string groundSha1 = WorldPackage.HashGrid(ground);

        // The lowest bit of one cell of 10,201: a server holding either grid with that one bit
        // changed answers another digest for it.
        string offsetFlipped = WorldPackage.HashGrid(WithOneBitFlipped(offset, 5000));
        string groundFlipped = WorldPackage.HashGrid(WithOneBitFlipped(ground, 5000));

        Assert.Equal($"the loaded world's surface offset grid has SHA-1 {offsetFlipped} and the "
                     + $"package's has SHA-1 {offsetSha1}",
                     Assert.Single(LoadedWorldCheck.Disagreements(world.PackagePath, loaded with
                     {
                         BareEarth = loaded.BareEarth! with { OffsetGridSha1 = offsetFlipped },
                     })));

        Assert.Equal($"the loaded world's bare-earth ground grid has SHA-1 {groundFlipped} and the "
                     + $"package's has SHA-1 {groundSha1}",
                     Assert.Single(LoadedWorldCheck.Disagreements(world.PackagePath, loaded with
                     {
                         BareEarth = loaded.BareEarth! with { GroundGridSha1 = groundFlipped },
                     })));

        // Both at once are both named, and the session is refused over them.
        LoadedWorld both = loaded with
        {
            BareEarth = loaded.BareEarth! with
            {
                OffsetGridSha1 = offsetFlipped,
                GroundGridSha1 = groundFlipped,
            },
        };
        Assert.Equal(2, LoadedWorldCheck.Disagreements(world.PackagePath, both).Count);
        CoSimSessionRefusedException refused = Assert.Throws<CoSimSessionRefusedException>(
            () => LoadedWorldCheck.Require(world.PackagePath, both));
        Assert.Contains(offsetFlipped, refused.Message);
        Assert.Contains(groundSha1, refused.Message);
    }

    [Fact]
    public void ADrapedWorldWhoseServerPublishesNoDigestIsRefused()
    {
        // A server built before it published the digests: the check has nothing to compare the
        // package's grids with, and must not take their silence for agreement.
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        LoadedWorld loaded = world.AsLoaded();
        LoadedWorld silent = loaded with
        {
            BareEarth = loaded.BareEarth! with { OffsetGridSha1 = string.Empty, GroundGridSha1 = string.Empty },
        };

        Assert.StartsWith("the loaded world's server publishes no digest of its bare-earth grids",
                          Assert.Single(LoadedWorldCheck.Disagreements(world.PackagePath, silent)));
    }

    [Fact]
    public void AWorldShiftedByAConstantIsComparedByItsConstantAloneAsBefore()
    {
        // No grids on either side, so no digests either: the constant is the whole surface.
        using SyntheticWorld world = SyntheticWorld.WriteShiftedByAConstant(-2.01);

        LoadedWorld loaded = world.AsLoaded();
        Assert.Equal(string.Empty, loaded.BareEarth!.OffsetGridSha1);
        Assert.Empty(LoadedWorldCheck.Disagreements(world.PackagePath, loaded));

        Assert.Equal("the loaded world's surface was shifted by -1.5 m and the package's by -2.01 m",
                     Assert.Single(LoadedWorldCheck.Disagreements(world.PackagePath, loaded with
                     {
                         BareEarth = loaded.BareEarth with { OffsetMetres = -1.5 },
                     })));
    }

    [Fact]
    public void APackageRecordsTheDigestsOfItsGridsAndIsAdmittedWithOrWithoutThem()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            at => 0.01 * at.X, CoSimFixtures.RightAngleTurnNetwork, "!");

        WorldPackageManifest manifest = WorldPackage.ReadManifest(world.PackagePath);
        Assert.True(WorldPackage.TryReadGrids(world.PackagePath, out float[] offset, out float[] ground));
        Assert.Equal(WorldPackage.HashGrid(offset), manifest.BareEarthOffsetSha1);
        Assert.Equal(WorldPackage.HashGrid(ground), manifest.BareEarthDtmSha1);
        Assert.Empty(LoadedWorldCheck.Disagreements(world.PackagePath, world.AsLoaded()));

        // A package written before the digests were recorded: nothing depends on them.
        RewriteManifest(world.PackagePath, recorded => recorded with
        {
            BareEarthOffsetSha1 = string.Empty,
            BareEarthDtmSha1 = string.Empty,
        });
        Assert.Empty(LoadedWorldCheck.Disagreements(world.PackagePath, world.AsLoaded()));
    }

    [Fact]
    public void APackageWhoseManifestRecordsAnotherGridThanItCarriesIsRefused()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            at => 0.01 * at.X, CoSimFixtures.RightAngleTurnNetwork, "!");
        using SyntheticWorld other = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        // The manifest names another world's ground; the grid entry, and the server, are this one's.
        string carried = GroundSha1(world);
        string recorded = GroundSha1(other);
        RewriteManifest(world.PackagePath, manifest => manifest with { BareEarthDtmSha1 = recorded });

        Assert.Equal($"the package's manifest records its bare-earth ground grid as SHA-1 {recorded} "
                     + $"and its bareearth.bin holds one with SHA-1 {carried}",
                     Assert.Single(LoadedWorldCheck.Disagreements(world.PackagePath, world.AsLoaded())));
    }

    [Fact]
    public void AWorldWhoseGridIsShiftedByACellIsRefused()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        LoadedWorld loaded = world.AsLoaded();
        LoadedWorld shifted = loaded with
        {
            BareEarth = loaded.BareEarth! with { MinXMetres = loaded.BareEarth.MinXMetres + 2.0 },
        };

        string disagreement = Assert.Single(LoadedWorldCheck.Disagreements(world.PackagePath, shifted));
        Assert.Equal("the loaded world's surface grid is 101x101 cells of 2 m from (-98, -100) and "
                     + "the package's is 101x101 cells of 2 m from (-100, -100)", disagreement);
    }

    [Fact]
    public void AWorldShiftedByAConstantIsRefusedAgainstADrapedPackage()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        LoadedWorld constant = world.AsLoaded() with
        {
            BareEarth = new BareEarthRecord(-2.01, false, 0.0, 0.0, 0.0, 0, 0, string.Empty, string.Empty),
        };

        string disagreement = Assert.Single(LoadedWorldCheck.Disagreements(world.PackagePath, constant));
        Assert.Equal("the loaded world's surface was shifted by a constant -2.01 m and the package's "
                     + "was draped cell by cell", disagreement);
    }

    [Fact]
    public void AWorldAtAnotherOriginIsRefusedAndOneAtTheSameOriginToTheLastBitIsNot()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        LoadedWorld loaded = world.AsLoaded();

        // A millionth of a degree is about eleven centimetres of latitude.
        string disagreement = Assert.Single(LoadedWorldCheck.Disagreements(
            world.PackagePath, loaded with { OriginLatitude = loaded.OriginLatitude + 1e-6 }));
        Assert.Equal("the loaded world's origin is 0.000001, 0 at 1000 m and the package's is 0, 0 "
                     + "at 1000 m", disagreement);

        Assert.Single(LoadedWorldCheck.Disagreements(
            world.PackagePath, loaded with { OriginHeightMetres = loaded.OriginHeightMetres + 0.01 }));

        // Within the margin: the last bits of a double, not a different place.
        Assert.Empty(LoadedWorldCheck.Disagreements(
            world.PackagePath, loaded with { OriginLongitude = loaded.OriginLongitude + 1e-12 }));
    }

    [Fact]
    public void AWorldServingAnotherRoadNetworkIsRefused()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        LoadedWorld loaded = world.AsLoaded();
        const string Other = "<OpenDRIVE><header revMajor=\"1\"/></OpenDRIVE>";

        string disagreement = Assert.Single(LoadedWorldCheck.Disagreements(
            world.PackagePath, loaded with { OpenDrive = Other }));
        Assert.Equal($"the loaded world serves an OpenDRIVE with digest "
                     + $"{WorldPackage.HashOpenDrive(Other)[..12]} and the package carries one with "
                     + $"digest {WorldPackage.HashOpenDrive("<OpenDRIVE/>")[..12]}", disagreement);

        Assert.Equal("the loaded world serves no OpenDRIVE", Assert.Single(LoadedWorldCheck.Disagreements(
            world.PackagePath, loaded with { OpenDrive = string.Empty })));
    }

    [ShippedWorldPackagesFact]
    public void EachShippedPackageCarriesTheOpenDriveItsManifestNames()
    {
        foreach (string package in new[]
                 {
                     ShippedWorldPackagesFactAttribute.Arapahoe!,
                     ShippedWorldPackagesFactAttribute.Gardnerville!,
                     ShippedWorldPackagesFactAttribute.Bahonar!,
                 })
        {
            Assert.Equal(WorldPackage.ReadManifest(package).OpenDriveSha256,
                         WorldPackage.HashOpenDrive(WorldPackage.ReadOpenDrive(package)));
        }
    }

    [ShippedWorldPackagesFact]
    public void EachShippedWorldIsItsOwnAndNotTheOthers()
    {
        string arapahoe = ShippedWorldPackagesFactAttribute.Arapahoe!;
        string gardnerville = ShippedWorldPackagesFactAttribute.Gardnerville!;
        string bahonar = ShippedWorldPackagesFactAttribute.Bahonar!;

        foreach (string package in new[] { arapahoe, gardnerville, bahonar })
        {
            Assert.Empty(LoadedWorldCheck.Disagreements(package, SyntheticWorld.Describe(package)));
        }

        // Gardnerville's package handed to a session on a server that has Arapahoe loaded.
        IReadOnlyList<string> disagreements =
            LoadedWorldCheck.Disagreements(gardnerville, SyntheticWorld.Describe(arapahoe));
        Assert.Equal(3, disagreements.Count);
        Assert.StartsWith("the loaded world's surface grid is 478x971 cells of 2 m", disagreements[0]);
        Assert.StartsWith("the loaded world's origin is 39.59431, -104.88449", disagreements[1]);
        Assert.StartsWith("the loaded world serves an OpenDRIVE with digest b6bb52668042", disagreements[2]);
    }

    [ShippedWorldPackagesFact]
    public void EachShippedPackageSGridEntryHashesAsItsGridsDo()
    {
        // The check hashes a package's grids from the entry's bytes as they stand; the server hashes
        // the floats it holds. On the real packages -- Bahonar's grids are 7,611,381 cells each --
        // the two agree, so the byte layout the digest is defined over is the one the entry has.
        foreach (string package in new[]
                 {
                     ShippedWorldPackagesFactAttribute.Arapahoe!,
                     ShippedWorldPackagesFactAttribute.Gardnerville!,
                     ShippedWorldPackagesFactAttribute.Bahonar!,
                 })
        {
            Assert.True(WorldPackage.TryReadGridDigests(package, out string offsetSha1, out string groundSha1));
            Assert.True(WorldPackage.TryReadGrids(package, out float[] offset, out float[] ground));
            Assert.Equal(WorldPackage.HashGrid(offset), offsetSha1);
            Assert.Equal(WorldPackage.HashGrid(ground), groundSha1);
        }

        // And both agree with an independent implementation: Python's hashlib over the slices of
        // Bahonar's bareearth.bin, measured with zipfile.
        Assert.True(WorldPackage.TryReadGridDigests(ShippedWorldPackagesFactAttribute.Bahonar!,
                                                    out string bahonarOffset, out string bahonarGround));
        Assert.Equal("d5bdd7150a20dd51b1f30751c95f136145e91251", bahonarOffset);
        Assert.Equal("a1e980fcdb88d68b879c9c5eac47446bef120619", bahonarGround);
    }

    /// <summary>The digest of the bare-earth ground grid a synthetic world's package carries.</summary>
    private static string GroundSha1(SyntheticWorld world)
    {
        Assert.True(WorldPackage.TryReadGrids(world.PackagePath, out _, out float[] ground));
        return WorldPackage.HashGrid(ground);
    }

    /// <summary>A copy of a grid with the lowest bit of one cell flipped.</summary>
    private static float[] WithOneBitFlipped(float[] grid, int cell)
    {
        float[] flipped = [.. grid];
        flipped[cell] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(flipped[cell]) ^ 1);
        return flipped;
    }

    /// <summary>
    /// Rewrite a package's manifest in place, as a package edited after it was written -- or one
    /// written before a field existed -- would carry it.
    /// </summary>
    private static void RewriteManifest(string packagePath,
                                        Func<WorldPackageManifest, WorldPackageManifest> change)
    {
        WorldPackageManifest rewritten = change(WorldPackage.ReadManifest(packagePath));
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        archive.GetEntry("world.json")!.Delete();
        using Stream entry = archive.CreateEntry("world.json", CompressionLevel.NoCompression).Open();
        entry.Write(new UTF8Encoding(false).GetBytes(
            JsonSerializer.Serialize(rewritten, new JsonSerializerOptions { WriteIndented = true })));
    }
}
