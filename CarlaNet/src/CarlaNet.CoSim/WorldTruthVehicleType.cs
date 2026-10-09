using System.Globalization;
using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// What the world truth track writes about one SUMO vehicle type: what kind of vehicle it is, the
/// dimensions and colour the scenario declared for it, and why it has no body where it has none.
/// </summary>
/// <param name="VehicleClass">SUMO's vehicle class for the type, as SUMO names it.</param>
/// <param name="BaseType">
/// The truth contract's base type: the one the catalogue curates for the blueprint the type names, and
/// only for a type that names no blueprint a catalogue class draws, its vehicle class's
/// (<see cref="BaseTypeOf"/>).
/// </param>
/// <param name="LengthMetres">The length the type declares, which SUMO's car-following ran on.</param>
/// <param name="WidthMetres">The width it declares.</param>
/// <param name="HeightMetres">The height it declares.</param>
/// <param name="Color">The type's colour, as <c>r,g,b</c>.</param>
/// <param name="SpecialType">
/// The kind the catalogue curates for the blueprint the type names; empty where it curates none, or the
/// type names no blueprint a catalogue class draws.
/// </param>
/// <param name="Unrenderable">Why the type has no measured body, or null where it has one.</param>
/// <remarks>
/// <para><b>Asked of SUMO once per type and kept.</b> A type's declaration does not change during a
/// run, so five round trips the first time a vehicle of it is seen are all it costs, however many
/// vehicles of it there are, and a sixth for a type with no measured body, to read the blueprint it
/// names; the body is the binder's, which asks its own question once per type.</para>
///
/// <para><b>The values the standalone producer writes for the same type</b>
/// (<c>CarlaControl/src/carlacontrol/SumoCotBridge.py</c>): the declared dimensions rather than the
/// body's measured box, which a capture sidecar carries for a vehicle drawn; the catalogue's base type
/// and kind for the blueprint the type names (doc 06 D6.18), and for a type that names none of the
/// catalogue's, the base type through the same table of vehicle classes and no kind. So the capture
/// path's track and the CARLA-free path's CSV compare column for column.</para>
/// </remarks>
internal sealed record WorldTruthVehicleType(
    string VehicleClass,
    string BaseType,
    double LengthMetres,
    double WidthMetres,
    double HeightMetres,
    string Color,
    string SpecialType,
    UnrenderableReason? Unrenderable)
{
    /// <summary>
    /// SUMO's vehicle classes as the truth contract's base types, the standalone producer's table, read
    /// only for a type that names no blueprint a catalogue class draws. A class not in it is reported as
    /// SUMO names it.
    /// </summary>
    private static readonly Dictionary<string, string> BaseTypeByVehicleClass = new(StringComparer.Ordinal)
    {
        ["passenger"] = "car",
        ["delivery"] = "van",
        ["truck"] = "truck",
        ["trailer"] = "truck",
        ["motorcycle"] = "motorcycle",
        ["moped"] = "motorcycle",
        ["bicycle"] = "bicycle",
        ["bus"] = "bus",
        ["coach"] = "bus",
        ["taxi"] = "car",
        ["emergency"] = "car",
    };

    /// <summary>The base type a SUMO vehicle class stands for.</summary>
    public static string BaseTypeOf(string vehicleClass) =>
        BaseTypeByVehicleClass.GetValueOrDefault(vehicleClass, vehicleClass);

    /// <summary>
    /// The base type and the kind the track writes for a type: the catalogue's for the blueprint the
    /// type names, where a catalogue class draws it, and otherwise the vehicle class's base type and no
    /// kind, SUMO reporting none.
    /// </summary>
    /// <param name="catalogue">The catalogue the kinds are read from.</param>
    /// <param name="blueprintId">The blueprint the type names, or an empty string where it names none.</param>
    /// <param name="vehicleClass">SUMO's vehicle class for the type.</param>
    internal static (string BaseType, string SpecialType) KindsOf(VehicleCatalogue catalogue, string blueprintId,
                                                                  string vehicleClass) =>
        (catalogue.BaseTypes.TryGetValue(blueprintId, out string? curated) ? curated : BaseTypeOf(vehicleClass),
         catalogue.SpecialTypes.GetValueOrDefault(blueprintId, string.Empty));

    /// <summary>Ask SUMO about a type, and the binder and the catalogue about its body.</summary>
    /// <param name="traci">The connection to the SUMO the session drives.</param>
    /// <param name="binder">The session's binder, which knows the measured body the type stands for.</param>
    /// <param name="catalogue">The catalogue the body's base type and kind are read from.</param>
    /// <param name="typeId">The type, as a vehicle's state names it.</param>
    public static WorldTruthVehicleType Read(TraCIConnection traci, VehicleTypeBinder binder,
                                             VehicleCatalogue catalogue, string typeId)
    {
        const int Get = TraCIConstants.CMD_GET_VEHICLETYPE_VARIABLE;
        string vehicleClass = traci.GetVariable(Get, TraCIConstants.VAR_VEHICLECLASS, typeId).AsString;
        double length = traci.GetVariable(Get, TraCIConstants.VAR_LENGTH, typeId).AsDouble;
        double width = traci.GetVariable(Get, TraCIConstants.VAR_WIDTH, typeId).AsDouble;
        double height = traci.GetVariable(Get, TraCIConstants.VAR_HEIGHT, typeId).AsDouble;
        (byte red, byte green, byte blue, _) = traci.GetVariable(Get, TraCIConstants.VAR_COLOR, typeId).AsColor;

        // The blueprint the type names: the binder's for a type with a measured body, and asked of SUMO
        // for one without, which may still name a blueprint a catalogue class draws.
        bool bound = binder.TryBind(typeId, out VehicleExtent extent);
        string blueprintId = bound
            ? extent.BlueprintId
            : traci.GetParameter(Get, typeId, VehicleCatalogue.BlueprintParameter);
        (string baseType, string specialType) = KindsOf(catalogue, blueprintId, vehicleClass);
        return new WorldTruthVehicleType(
            vehicleClass,
            baseType,
            length,
            width,
            height,
            string.Create(CultureInfo.InvariantCulture, $"{red},{green},{blue}"),
            specialType,
            bound ? null : binder.RefusedTypes[typeId]);
    }
}
