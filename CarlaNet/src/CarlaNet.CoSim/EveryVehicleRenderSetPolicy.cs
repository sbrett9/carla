using System.Globalization;

namespace CarlaNet.CoSim;

/// <summary>
/// Every vehicle SUMO has holds a body: the session's default, which with no capacity limits nothing.
/// </summary>
/// <remarks>
/// <para><b>No capacity, the default.</b> SUMO's scenario is the only arbiter of population: a vehicle
/// holds a body from the frame SUMO first reports it in to the frame of the last step SUMO reports it in,
/// or until the session ends, parked vehicles included, and vehicles new to the set are admitted in
/// ordinal order of their ids, so two runs of one scenario lend their bodies in the same order. A heavier
/// scenario makes a synchronous run slower on the wall clock, never thinner.</para>
///
/// <para><b>With a capacity</b>, an optional performance control: at most that many hold a body at
/// once. A vehicle holding one keeps it until SUMO removes it, and a newcomer takes a place only when
/// one is free, the newcomers ranked by their place in the scenario seed's order
/// (<see cref="SeededOrder"/>): fixed for a vehicle's life and blind to where it is, so the vehicles
/// drawn are an unbiased sample of the ones SUMO has, and one seed always draws the same sample. A
/// vehicle left out has no body and no truth record, and the report counts it. A vehicle that leaves
/// SUMO gives up its place at the pass that finds it gone and still keeps its body for the frame of its
/// last step, so on that one frame the bodies drawn can exceed the capacity by the vehicles leaving SUMO
/// at that step.</para>
/// </remarks>
public sealed class EveryVehicleRenderSetPolicy : IRenderSetPolicy
{
    private long _seed;

    /// <param name="capacity">How many vehicles may hold a body at once, or null for no limit.</param>
    public EveryVehicleRenderSetPolicy(int? capacity = null)
    {
        if (capacity is { } count)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count, nameof(capacity));
        }

        Capacity = capacity;
    }

    /// <inheritdoc/>
    public int? Capacity { get; }

    /// <inheritdoc/>
    public bool Limits => Capacity is not null;

    /// <inheritdoc/>
    public double AdmitLeadSeconds => 0.0;

    /// <inheritdoc/>
    public double ReleaseLagSeconds => 0.0;

    /// <inheritdoc/>
    public RenderSetRule ActiveRule => RenderSetRule.Every;

    /// <inheritdoc/>
    public IReadOnlyList<CameraFootprint> Footprints => [];

    /// <inheritdoc/>
    public string Description =>
        Capacity is { } capacity
            ? "every vehicle SUMO has, up to " + capacity.ToString(CultureInfo.InvariantCulture)
              + " at once: a vehicle drawn keeps its body, and a newcomer takes a free place in the "
              + "seed's order"
            : "every vehicle SUMO has";

    /// <inheritdoc/>
    /// <remarks>The seed is all it takes, and only a capacity reads it.</remarks>
    public void BeginPass(RenderSetPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        _seed = pass.Seed;
    }

    /// <inheritdoc/>
    public bool ShouldRender(in CoSimVehicleFrame frame, bool alreadyRendered) => true;

    /// <inheritdoc/>
    /// <remarks>
    /// A vehicle drawn ranks ahead of a newcomer, so a place is never taken from it. With no capacity
    /// every newcomer ranks the same, and the tie on the id admits them in ordinal order; with one, they
    /// rank by the seed's order.
    /// </remarks>
    public double Rank(in CoSimVehicleFrame frame, bool alreadyRendered) =>
        (alreadyRendered ? 0.0 : 2.0) + (Capacity is null ? 0.0 : SeededOrder.Of(_seed, frame.Id));
}
