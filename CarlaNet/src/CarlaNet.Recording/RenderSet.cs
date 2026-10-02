namespace CarlaNet.Recording;

/// <summary>
/// One body a frame rendered, and the vehicle it rendered on that frame.
/// </summary>
/// <param name="ActorId">The body: the CARLA actor the frame drew.</param>
/// <param name="SumoId">
/// The SUMO vehicle the body rendered on this frame. Stable for the vehicle's whole life, where the
/// actor id names each vehicle the body carries over a run in turn.
/// </param>
/// <param name="VehicleTypeId">The vehicle type the scenario's <c>vType</c> declared for it.</param>
/// <param name="AdmittedTick">
/// The first frame of the span this vehicle has been rendered over without a break: the frame its
/// body was first drawn for it, so a track that begins mid-scene can be told from one that entered.
/// </param>
public sealed record RenderedVehicle(uint ActorId, string SumoId, string VehicleTypeId, ulong AdmittedTick)
{
    /// <summary>
    /// The angle SUMO reported for the vehicle at the frame, degrees clockwise from north, where the
    /// render set came from the session in this process; null where it came from the server, which is
    /// told the set only when a body is lent or given back. Audit beside the body's own heading, which
    /// is its transform's.
    /// </summary>
    public double? SumoAngleDegrees { get; init; }
}

/// <summary>
/// The bodies one frame rendered, each with the vehicle it rendered.
/// </summary>
/// <remarks>
/// Immutable once built, so a source can hand the same set out for every frame until a body is lent
/// or given back, and a reader on another thread never sees one half-changed.
/// </remarks>
public sealed class RenderSet
{
    private readonly Dictionary<uint, RenderedVehicle> _byActor;

    /// <param name="vehicles">Every body the frame rendered. A body appears once.</param>
    public RenderSet(IEnumerable<RenderedVehicle> vehicles)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        _byActor = [];
        foreach (RenderedVehicle vehicle in vehicles)
        {
            _byActor.Add(vehicle.ActorId, vehicle);
        }
    }

    /// <summary>How many bodies the frame rendered.</summary>
    public int Count => _byActor.Count;

    /// <summary>
    /// How far from a camera, in metres, the frame drew a body, or null where it drew every body at
    /// any range: no draw distance was set, or the server refused it.
    /// </summary>
    /// <remarks>
    /// A body farther than this from a camera is in the set -- it is posed and in the world -- and is
    /// not in that camera's image, so a recorder marks it in that camera's sidecar rather than listing
    /// it as seen (<see cref="DrawDistanceReach"/>).
    /// </remarks>
    public double? DrawDistanceMetres { get; init; }

    /// <summary>Every body the frame rendered, by actor id.</summary>
    public IReadOnlyDictionary<uint, RenderedVehicle> ByActor => _byActor;

    /// <summary>The vehicle a body rendered on this frame, where it rendered one.</summary>
    public bool TryGet(uint actorId, out RenderedVehicle vehicle) =>
        _byActor.TryGetValue(actorId, out vehicle!);

    /// <summary>
    /// The truth records of this frame's bodies, each carrying the vehicle it rendered, and nothing
    /// else.
    /// </summary>
    /// <param name="records">Every vehicle actor's truth record, as of this set's frame.</param>
    /// <param name="missing">Bodies this set holds that no record describes.</param>
    /// <remarks>
    /// A vehicle actor outside the set was drawn for no vehicle on this frame: a body standing in its
    /// parking slot, out of sight below the ground, which a camera cannot see and a sensor must not be
    /// told is there. It is left out rather than reported. A body the set holds and no record
    /// describes is a vehicle in the picture with no truth beside it, which is counted rather than
    /// passed over.
    /// </remarks>
    public IReadOnlyList<VehicleTelemetry> Select(IReadOnlyList<VehicleTelemetry> records, out int missing)
    {
        ArgumentNullException.ThrowIfNull(records);
        var selected = new List<VehicleTelemetry>(Math.Min(records.Count, _byActor.Count));
        foreach (VehicleTelemetry record in records)
        {
            if (_byActor.TryGetValue(record.Id, out RenderedVehicle? rendered))
            {
                selected.Add(record with { Rendered = rendered });
            }
        }

        missing = _byActor.Count - selected.Count;
        return selected;
    }
}
