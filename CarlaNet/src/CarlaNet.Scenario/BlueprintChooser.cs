using CarlaNet.Types.Rpc.Actors;

namespace CarlaNet.Scenario;

/// <summary>
/// Picks the vehicle blueprint an entity is placed as.
///
/// A storyboard describes an entity by category, and the authoring tool adds the name of its own
/// template. Neither names a blueprint in this world, so the hint is honoured only when it happens to
/// match one and the category decides otherwise.
/// </summary>
public static class BlueprintChooser
{
    /// Ordinary passenger cars, preferred over the emergency vehicles and heavy goods vehicles a bare
    /// category match would also admit. Order is preference order.
    private static readonly Dictionary<string, string[]> PreferredByCategory =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["car"] = ["impala", "mkz", "cooper", "patrol", "mustang", "audi", "bmw", "mercedes"],
            ["truck"] = ["carlacola", "fuso", "firetruck"],
            ["van"] = ["sprinter"],
            ["bus"] = ["sprinter"],
            ["motorbike"] = ["harley", "yamaha", "kawasaki"],
            ["bicycle"] = ["crossbike", "omafiets", "diamondback"],
        };

    /// <summary>Chooses a blueprint and builds the description used to place the entity.</summary>
    /// <exception cref="ScenarioParseException">The world offers no vehicle blueprints.</exception>
    public static ActorDescription Describe(IReadOnlyList<ActorDefinition> catalogue, ScenarioEntity entity)
    {
        ActorDefinition definition = Choose(catalogue, entity);

        var attributes = (definition.Attributes ?? [])
            .Select(a => a.Id == "color" && !string.IsNullOrEmpty(entity.Colour)
                ? new ActorAttributeValue(a.Id, a.Type, entity.Colour!)
                : new ActorAttributeValue(a.Id, a.Type, a.Value))
            .ToList();

        return new ActorDescription(definition.Uid, definition.Id, attributes);
    }

    /// <summary>The blueprint an entity would be placed as.</summary>
    public static ActorDefinition Choose(IReadOnlyList<ActorDefinition> catalogue, ScenarioEntity entity)
    {
        var vehicles = new List<ActorDefinition>();
        foreach (ActorDefinition d in catalogue)
            if (d.Id is not null && d.Id.StartsWith("vehicle.", StringComparison.Ordinal))
                vehicles.Add(d);

        if (vehicles.Count == 0)
            throw new ScenarioParseException("the world offers no vehicle blueprints");

        if (!string.IsNullOrEmpty(entity.TemplateHint)
            && Matching(vehicles, entity.TemplateHint!) is { } byHint)
            return byHint;

        if (PreferredByCategory.TryGetValue(entity.Category ?? "car", out string[]? preferred))
            foreach (string want in preferred)
                if (Matching(vehicles, want) is { } byCategory)
                    return byCategory;

        return vehicles[0];
    }

    /// <summary>
    /// First blueprint whose identifier contains <paramref name="fragment"/>, or null.
    ///
    /// Written as a loop rather than with FirstOrDefault because a blueprint definition is a value
    /// type: FirstOrDefault yields a default instance when nothing matches, which is indistinguishable
    /// from a match by a null test and carries no attributes.
    /// </summary>
    private static ActorDefinition? Matching(List<ActorDefinition> vehicles, string fragment)
    {
        foreach (ActorDefinition d in vehicles)
            if (d.Id.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }
}
