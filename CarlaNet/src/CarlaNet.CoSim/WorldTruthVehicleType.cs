using System.Globalization;
using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// What the world truth track writes about one SUMO vehicle type: what kind of vehicle it is, the
/// dimensions and colour the scenario declared for it, the kind the catalogue curates for its body, and
/// why it has no body where it has none.
/// </summary>
/// <param name="VehicleClass">SUMO's vehicle class for the type, as SUMO names it.</param>
/// <param name="BaseType">The truth contract's base type, from the vehicle class (<see cref="BaseTypeOf"/>).</param>
/// <param name="LengthMetres">The length the type declares, which SUMO's car-following ran on.</param>
/// <param name="WidthMetres">The width it declares.</param>
/// <param name="HeightMetres">The height it declares.</param>
/// <param name="Color">The type's colour, as <c>r,g,b</c>.</param>
/// <param name="SpecialType">
/// The kind the catalogue curates for the blueprint the type's body is drawn from; empty where it curates
/// none, or the type has no measured body.
/// </param>
/// <param name="Unrenderable">Why the type has no measured body, or null where it has one.</param>
/// <remarks>
/// <para><b>Asked of SUMO once per type and kept.</b> A type's declaration does not change during a
/// run, so five round trips the first time a vehicle of it is seen are all it costs, however many
/// vehicles of it there are; the body is the binder's, which asks its own question once per type.</para>
///
/// <para><b>The values the standalone producer writes for the same type</b>
/// (<c>CarlaControl/src/carlacontrol/SumoCotBridge.py</c>): the declared dimensions rather than the
/// body's measured box, which a capture sidecar carries for a vehicle drawn; the base type through the
/// same table of vehicle classes; the catalogue's kind for the blueprint. So the capture path's track
/// and the CARLA-free path's CSV compare column for column.</para>
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
    /// SUMO's vehicle classes as the truth contract's base types, the standalone producer's table. A
    /// class not in it is reported as SUMO names it.
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

    /// <summary>Ask SUMO about a type, and the binder and the catalogue about its body.</summary>
    /// <param name="traci">The connection to the SUMO the session drives.</param>
    /// <param name="binder">The session's binder, which knows the measured body the type stands for.</param>
    /// <param name="catalogue">The catalogue the body's kind is read from.</param>
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

        bool bound = binder.TryBind(typeId, out VehicleExtent extent);
        return new WorldTruthVehicleType(
            vehicleClass,
            BaseTypeOf(vehicleClass),
            length,
            width,
            height,
            string.Create(CultureInfo.InvariantCulture, $"{red},{green},{blue}"),
            bound ? catalogue.SpecialTypes.GetValueOrDefault(extent.BlueprintId, string.Empty) : string.Empty,
            bound ? null : binder.RefusedTypes[typeId]);
    }
}
