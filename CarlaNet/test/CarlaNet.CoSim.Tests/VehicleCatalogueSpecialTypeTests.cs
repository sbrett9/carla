namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What kind of vehicle each body is, as the catalogue curates it: the truth record's
/// <c>special_type</c> comes from the catalogue's class for the body's blueprint (doc 06 D6.18).
/// </summary>
public sealed class VehicleCatalogueSpecialTypeTests
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
        // Swept and failed, so no class draws it: its truth keeps the kind its blueprint declares.
        Assert.False(catalogue.SpecialTypes.ContainsKey("vehicle.carlacola.actors"));
    }

    [Fact]
    public void ACatalogueWithNoClassesCuratesNoKind()
    {
        VehicleCatalogue catalogue = VehicleCatalogue.Parse("""{ "catalogue_version": 1 }""");

        Assert.Empty(catalogue.SpecialTypes);
    }
}
