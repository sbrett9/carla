namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What kind of vehicle each body is, as the catalogue curates it: the truth record's <c>base_type</c>
/// and <c>special_type</c> come from the catalogue's class for the body's blueprint (doc 06 D6.18).
/// </summary>
public sealed class VehicleCatalogueKindTests
{
    [Fact]
    public void TheShippedCatalogueGivesEveryBlueprintItsClassSKind()
    {
        VehicleCatalogue catalogue = VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue);

        // Every measured blueprint is drawn by a class, so every one has a kind, most of them empty.
        Assert.Equal(catalogue.MeasuredBlueprintIds.Order(StringComparer.Ordinal),
                     catalogue.SpecialTypes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("taxi", catalogue.SpecialTypes["vehicle.taxi.ford"]);
        Assert.Equal("emergency", catalogue.SpecialTypes["vehicle.ambulance.ford"]);
        Assert.Equal("emergency", catalogue.SpecialTypes["vehicle.dodgecop.charger"]);
        Assert.Equal("emergency", catalogue.SpecialTypes["vehicle.firetruck.actors"]);
        Assert.Equal(4, catalogue.SpecialTypes.Values.Count(kind => kind.Length > 0));
        Assert.Equal(string.Empty, catalogue.SpecialTypes["vehicle.ue4.ford.crown"]);
        Assert.Equal(string.Empty, catalogue.SpecialTypes["vehicle.fuso.mitsubishi"]);
    }

    [Fact]
    public void TheShippedCatalogueGivesEveryBlueprintItsClassSBaseType()
    {
        VehicleCatalogue catalogue = VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue);

        // Every measured blueprint has a base type, and the ones SUMO's vehicle class would misname are
        // the catalogue's: the ambulance a van and the fire appliance a truck, where the emergency class
        // reads as a car; the police car a car, where the authority class reads as nothing at all; the
        // light bus a bus, where its declared truck class reads as a truck.
        Assert.Equal(catalogue.MeasuredBlueprintIds.Order(StringComparer.Ordinal),
                     catalogue.BaseTypes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("van", catalogue.BaseTypes["vehicle.ambulance.ford"]);
        Assert.Equal("truck", catalogue.BaseTypes["vehicle.firetruck.actors"]);
        Assert.Equal("car", catalogue.BaseTypes["vehicle.dodgecop.charger"]);
        Assert.Equal("bus", catalogue.BaseTypes["vehicle.fuso.mitsubishi"]);
        Assert.Equal("van", catalogue.BaseTypes["vehicle.sprinter.mercedes"]);
        Assert.Equal("car", catalogue.BaseTypes["vehicle.jeep.wrangler_rubicon"]);
        Assert.Equal("truck", catalogue.BaseTypes["vehicle.carlamotors.european_hgv"]);
    }

    [Fact]
    public void AClassThatCuratesNoKindGivesItsMembersAnEmptyOneAndABlueprintNoClassDrawsHasNone()
    {
        VehicleCatalogue catalogue = VehicleCatalogue.Parse("""
            {
              "catalogue_version": 1,
              "catalogue_id": "test",
              "vehicles": [
                { "blueprint_id": "vehicle.taxi.ford", "measurement": "measured",
                  "length_m": 5.354, "width_m": 1.789, "height_m": 1.575,
                  "bbox_centre_m": [0.180, 0.0, 0.7] },
                { "blueprint_id": "vehicle.ue4.ford.crown", "measurement": "measured",
                  "length_m": 5.366, "width_m": 1.801, "height_m": 1.575,
                  "bbox_centre_m": [0.185, 0.0, 0.7] },
                { "blueprint_id": "vehicle.carlacola.actors", "measurement": "failed",
                  "measurement_note": "did not spawn" }
              ],
              "classes": [
                { "class_id": "taxi", "cot_base_type": "car", "cot_special_type": "taxi",
                  "members": [ { "blueprint_id": "vehicle.taxi.ford", "weight": 1.0 } ] },
                { "class_id": "civ_car", "cot_base_type": "car",
                  "members": [ { "blueprint_id": "vehicle.ue4.ford.crown", "weight": 1.0 } ] }
              ]
            }
            """);

        Assert.Equal(2, catalogue.SpecialTypes.Count);
        Assert.Equal("taxi", catalogue.SpecialTypes["vehicle.taxi.ford"]);
        Assert.Equal(string.Empty, catalogue.SpecialTypes["vehicle.ue4.ford.crown"]);
        Assert.Equal(2, catalogue.BaseTypes.Count);
        Assert.Equal("car", catalogue.BaseTypes["vehicle.taxi.ford"]);
        Assert.Equal("car", catalogue.BaseTypes["vehicle.ue4.ford.crown"]);
        // Swept and failed, so no class draws it: its truth keeps what its blueprint declares.
        Assert.False(catalogue.SpecialTypes.ContainsKey("vehicle.carlacola.actors"));
        Assert.False(catalogue.BaseTypes.ContainsKey("vehicle.carlacola.actors"));
    }

    [Fact]
    public void AClassWithNoBaseTypeLeavesItsMembersToTheirBlueprintsRatherThanGivingThemAnEmptyOne()
    {
        // The field is required, so this is a malformed catalogue: an empty base type in the truth would
        // be a record of no kind at all, where the blueprint's own declaration is at least a kind.
        VehicleCatalogue catalogue = VehicleCatalogue.Parse("""
            {
              "catalogue_version": 1,
              "classes": [
                { "class_id": "taxi", "cot_special_type": "taxi",
                  "members": [ { "blueprint_id": "vehicle.taxi.ford", "weight": 1.0 } ] }
              ]
            }
            """);

        Assert.Empty(catalogue.BaseTypes);
        Assert.Equal("taxi", catalogue.SpecialTypes["vehicle.taxi.ford"]);
    }

    [Fact]
    public void ACatalogueWithNoClassesCuratesNoKind()
    {
        VehicleCatalogue catalogue = VehicleCatalogue.Parse("""{ "catalogue_version": 1 }""");

        Assert.Empty(catalogue.SpecialTypes);
        Assert.Empty(catalogue.BaseTypes);
    }

    [Theory]
    // A type naming a catalogue blueprint takes the catalogue's base type and kind, whatever its class.
    [InlineData("vehicle.ambulance.ford", "emergency", "van", "emergency")]
    [InlineData("vehicle.firetruck.actors", "emergency", "truck", "emergency")]
    [InlineData("vehicle.dodgecop.charger", "authority", "car", "emergency")]
    [InlineData("vehicle.fuso.mitsubishi", "truck", "bus", "")]
    [InlineData("vehicle.jeep.wrangler_rubicon", "army", "car", "")]
    // One naming none, or one no catalogue class draws, takes its class's base type and no kind.
    [InlineData("", "passenger", "car", "")]
    [InlineData("", "emergency", "car", "")]
    [InlineData("vehicle.bmw.isetta", "passenger", "car", "")]
    [InlineData("vehicle.bmw.isetta", "army", "army", "")]
    public void TheWorldTrackTakesATypeSKindsFromTheCatalogueAndOnlyOtherwiseFromItsClass(
        string blueprintId, string vehicleClass, string baseType, string specialType)
    {
        VehicleCatalogue catalogue = VehicleCatalogue.Load(CoSimFixtures.VehicleCatalogue);

        Assert.Equal((baseType, specialType), WorldTruthVehicleType.KindsOf(catalogue, blueprintId, vehicleClass));
    }
}
