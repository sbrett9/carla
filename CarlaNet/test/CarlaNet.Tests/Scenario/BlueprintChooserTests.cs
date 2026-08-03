using CarlaNet.Scenario;
using CarlaNet.Types.Rpc.Actors;

namespace CarlaNet.Tests.Scenario;

/// <summary>
/// Covers choosing the blueprint an entity is placed as, against a catalogue shaped like the one this
/// fork's worlds offer: seventeen vehicles, none of whose identifiers resemble an authoring-tool
/// template name.
/// </summary>
public class BlueprintChooserTests
{
    private static ActorDefinition Vehicle(uint uid, string id) => new(
        uid, id, "", [new ActorAttribute("color", ActorAttributeType.RGBColor, "0,0,0", [], true, false)]);

    private static readonly List<ActorDefinition> Catalogue =
    [
        new(100, "sensor.camera.rgb", "", []),
        Vehicle(1, "vehicle.lincoln.mkz"),
        Vehicle(2, "vehicle.mini.cooper"),
        Vehicle(3, "vehicle.carlacola.actors"),
        Vehicle(4, "vehicle.ue4.chevrolet.impala"),
        Vehicle(5, "vehicle.firetruck.actors"),
    ];

    private static ScenarioEntity Entity(string category, string? hint = null, string? colour = null) => new()
    {
        Name = "Vehicle1",
        Category = category,
        TemplateHint = hint,
        Colour = colour,
        InitialPosition = new LanePosition(243, -1, 70.0, 0.0),
    };

    /// The failure that motivated extracting this: a blueprint definition is a value type, so a search
    /// that finds nothing yields a default instance rather than null. Treating that as a match placed an
    /// entity as a blueprint with no identifier and no attributes.
    [Fact]
    public void FallsBackToCategoryWhenTheTemplateHintMatchesNothing()
    {
        ActorDefinition chosen = BlueprintChooser.Choose(Catalogue, Entity("car", hint: "sedan"));

        Assert.Equal("vehicle.ue4.chevrolet.impala", chosen.Id);
        Assert.NotNull(chosen.Attributes);
    }

    [Fact]
    public void NeverChoosesADefaultDefinition()
    {
        foreach (string category in new[] { "car", "truck", "van", "bus", "unrecognised" })
        {
            ActorDefinition chosen = BlueprintChooser.Choose(Catalogue, Entity(category, hint: "nothing-matches"));
            Assert.False(string.IsNullOrEmpty(chosen.Id));
            Assert.StartsWith("vehicle.", chosen.Id);
        }
    }

    [Fact]
    public void HonoursATemplateHintThatDoesMatch()
        => Assert.Equal("vehicle.mini.cooper",
                        BlueprintChooser.Choose(Catalogue, Entity("car", hint: "cooper")).Id);

    [Fact]
    public void PrefersAnOrdinaryCarOverEmergencyAndGoodsVehicles()
    {
        ActorDefinition chosen = BlueprintChooser.Choose(Catalogue, Entity("car"));
        Assert.Equal("vehicle.ue4.chevrolet.impala", chosen.Id);
    }

    [Fact]
    public void ChoosesAGoodsVehicleForAGoodsCategory()
        => Assert.Equal("vehicle.carlacola.actors", BlueprintChooser.Choose(Catalogue, Entity("truck")).Id);

    [Fact]
    public void IgnoresNonVehicleBlueprints()
    {
        foreach (string category in new[] { "car", "truck", "bicycle" })
            Assert.StartsWith("vehicle.", BlueprintChooser.Choose(Catalogue, Entity(category)).Id);
    }

    [Fact]
    public void AppliesTheAuthoredColourWhereTheBlueprintTakesOne()
    {
        ActorDescription description = BlueprintChooser.Describe(Catalogue, Entity("car", colour: "255,0,0"));
        ActorAttributeValue colour = Assert.Single(description.Attributes, a => a.Id == "color");
        Assert.Equal("255,0,0", colour.Value);
    }

    [Fact]
    public void RefusesAWorldWithNoVehicles()
        => Assert.Throws<ScenarioParseException>(
            () => BlueprintChooser.Choose([new(100, "sensor.camera.rgb", "", [])], Entity("car")));
}
