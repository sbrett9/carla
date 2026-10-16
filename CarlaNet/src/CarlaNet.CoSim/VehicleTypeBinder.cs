using CarlaNet.Sumo;

namespace CarlaNet.CoSim;

/// <summary>
/// Binds a scenario's SUMO vehicle types to the measured bodies the catalogue holds, asking SUMO
/// once per type.
/// </summary>
/// <remarks>
/// <para>Which blueprint a vType stands for is written on the vType as a <c>&lt;param&gt;</c>, which
/// SUMO carries and does not interpret. It is read over TraCI rather than by parsing the scenario's
/// route files, because SUMO resolves which files declare which types and a second implementation of
/// that resolution would disagree with it sooner or later.</para>
///
/// <para>A type is asked about once and the answer kept, including a refusal. A scenario with four
/// types and sixty-eight thousand vehicles asks four questions.</para>
/// </remarks>
public sealed class VehicleTypeBinder
{
    private readonly TraCIConnection _traci;
    private readonly VehicleCatalogue _catalogue;
    private readonly Dictionary<string, VehicleExtent> _bound = [];
    private readonly Dictionary<string, UnrenderableReason> _refused = [];

    public VehicleTypeBinder(TraCIConnection connection, VehicleCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(catalogue);
        _traci = connection;
        _catalogue = catalogue;
    }

    /// <summary>Vehicle types bound to a measured body.</summary>
    public IReadOnlyDictionary<string, VehicleExtent> BoundTypes => _bound;

    /// <summary>Vehicle types that cannot be rendered, and why.</summary>
    public IReadOnlyDictionary<string, UnrenderableReason> RefusedTypes => _refused;

    /// <summary>
    /// The measured body a vehicle type stands for, or <see langword="false"/> where it has none.
    /// </summary>
    /// <remarks>
    /// A refusal is not an error here. A vehicle whose type carries no measured body is simulated,
    /// has truth worth recording, and is simply never rendered -- which is the only honest thing to
    /// do with it, because placing a body of unknown size at a pose computed from a number nobody
    /// measured produces imagery and a truth record that agree with each other and are both wrong.
    /// </remarks>
    public bool TryBind(string vehicleTypeId, out VehicleExtent extent)
    {
        ArgumentException.ThrowIfNullOrEmpty(vehicleTypeId);

        if (_bound.TryGetValue(vehicleTypeId, out extent))
        {
            return true;
        }

        if (_refused.ContainsKey(vehicleTypeId))
        {
            return false;
        }

        string blueprintId = _traci.GetParameter(TraCIConstants.CMD_GET_VEHICLETYPE_VARIABLE,
                                                 vehicleTypeId,
                                                 VehicleCatalogue.BlueprintParameter);
        if (_catalogue.TryResolve(vehicleTypeId, blueprintId, out extent,
                                  out UnrenderableReason reason))
        {
            _bound[vehicleTypeId] = extent;
            return true;
        }

        _refused[vehicleTypeId] = reason;
        return false;
    }
}
