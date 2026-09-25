using CarlaNet.Map.WorldPackage;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// Whether a world package describes the world a server has loaded: every way it can fail to, and
/// the one way it can succeed.
/// </summary>
/// <remarks>
/// The loaded world is what a server holding a given package's world answers
/// (<see cref="SyntheticWorld.Describe"/>), changed in exactly one respect per test, so each refusal
/// is attributable to the one thing that differs. The last tests point the check at the real
/// packages in <c>Build/world-packages</c>: one real world against another, and each against itself.
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

        // 101 columns from -100 m in 2 m cells: the fifty east of zero, on every one of 101 rows.
        Assert.StartsWith("5050 of 10201 cells of the loaded world's bare-earth ground grid differ",
                          disagreement);
        Assert.Contains("the first at column 51, row 0: 1001 m against 1000 m", disagreement);
    }

    [Fact]
    public void OneCellOfTheSurfaceOffsetDifferingIsEnough()
    {
        using SyntheticWorld world = SyntheticWorld.Write(
            _ => 0.0, CoSimFixtures.RightAngleTurnNetwork, "!");

        LoadedWorld loaded = world.AsLoaded();
        float[] offset = [.. loaded.BareEarth!.OffsetGrid];
        offset[5000] = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(offset[5000]) + 1);

        string disagreement = Assert.Single(LoadedWorldCheck.Disagreements(
            world.PackagePath, loaded with { BareEarth = loaded.BareEarth with { OffsetGrid = offset } }));
        Assert.StartsWith("1 of 10201 cells of the loaded world's surface offset grid differ", disagreement);
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
            BareEarth = new BareEarthRecord(-2.01, false, 0.0, 0.0, 0.0, 0, 0, [], []),
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
}
