namespace CarlaNet.CoSim;

/// <summary>
/// Which of the vehicles SUMO has hold a body in CARLA, and how many may at once.
/// </summary>
/// <remarks>
/// <para><b>By default, every one of them.</b> <see cref="EveryVehicleRenderSetPolicy"/> with no
/// capacity is the session's default and limits nothing: SUMO's scenario is the arbiter of population.
/// A circle (<see cref="RegionRenderSetPolicy"/>), the registered cameras' footprints
/// (<see cref="CameraFootprintRenderSetPolicy"/>) and a capacity are optional performance controls, off
/// unless chosen, that trade what is drawn for speed. A vehicle a limit leaves out is still simulated
/// by SUMO; it has no body, is in no frame and is in no truth record, and the report counts what was
/// left out (<see cref="Limits"/>).</para>
///
/// <para><b>Every vehicle stays subscribed whatever the policy.</b> SUMO delivers every vehicle's full
/// state every step, so the policy decides from the state itself, and a vehicle admitted part-way
/// through its drive has the frame before its admission and is drawn from the admission at its
/// interpolated pose; one SUMO inserts is drawn from the frame SUMO first reports it in, as it is with
/// no limit. A limit saves CARLA's rendering and posing, not SUMO's step.</para>
///
/// <para><b>The predicate takes what the caller already knows about the vehicle</b>, because without
/// it there is no hysteresis, and without hysteresis a vehicle on a boundary flickers in and out on
/// consecutive passes. With no dissolve on entry or exit that is the most visible failure a limit has,
/// so the shape of the interface makes the implementer answer the question.</para>
///
/// <para><b>Each pass begins by telling the policy what the session holds</b> (<see cref="BeginPass"/>):
/// the instant, SUMO's step, the seed, the bodies' reach and the cameras. A policy that decides from a
/// fixed region ignores all of it but the seed; one that follows the cameras decides from little else.
/// The time a released vehicle is held for (<see cref="ReleaseLagSeconds"/>) is the render-set
/// manager's to keep, so that every policy's releases are timed and recorded the same way.</para>
/// </remarks>
public interface IRenderSetPolicy
{
    /// <summary>How many vehicles may hold a body at once, or null for no limit on the count.</summary>
    int? Capacity { get; }

    /// <summary>
    /// Whether the policy can leave any vehicle SUMO has without a body: a capacity is set, or it
    /// renders less than every vehicle. False only for every vehicle with no capacity, the default.
    /// </summary>
    bool Limits { get; }

    /// <summary>
    /// Simulated seconds of travel ahead of a camera's footprint a vehicle is admitted at, under the rule
    /// in force at this pass; zero for a rule with no cameras.
    /// </summary>
    double AdmitLeadSeconds { get; }

    /// <summary>
    /// Simulated seconds a rendered vehicle that has stopped passing <see cref="ShouldRender"/> keeps its
    /// place for, under the rule in force at this pass, before it is released; zero releases it at the
    /// first pass it fails.
    /// </summary>
    double ReleaseLagSeconds { get; }

    /// <summary>The rule the last <see cref="BeginPass"/> put in force.</summary>
    RenderSetRule ActiveRule { get; }

    /// <summary>The footprint of every camera the last pass decided from; empty under any other rule.</summary>
    IReadOnlyList<CameraFootprint> Footprints { get; }

    /// <summary>What the policy is, in the report's words.</summary>
    string Description { get; }

    /// <summary>
    /// Take the session's state for the pass about to be made, before the predicate is asked about any
    /// vehicle.
    /// </summary>
    void BeginPass(RenderSetPass pass);

    /// <summary>Whether a vehicle should hold a body.</summary>
    /// <param name="frame">The vehicle's state for the SUMO step just read.</param>
    /// <param name="alreadyRendered">
    /// Whether it holds one now. An implementation that answers the same for both values has no
    /// hysteresis and will oscillate.
    /// </param>
    bool ShouldRender(in CoSimVehicleFrame frame, bool alreadyRendered);

    /// <summary>
    /// The order to admit in, lowest first, which decides who is left out when more vehicles pass
    /// <see cref="ShouldRender"/> than <see cref="Capacity"/> allows. Ties are broken on the vehicle id.
    /// </summary>
    /// <param name="frame">The vehicle's state for the SUMO step just read.</param>
    /// <param name="alreadyRendered">
    /// Whether it holds a place now, which a policy may prefer so that a newcomer does not take the
    /// place of a vehicle already in view.
    /// </param>
    /// <remarks>
    /// Must be a pure function of the frame, of whether the vehicle is rendered and of session-fixed
    /// inputs -- the seed among them. Two runs of one seed that admit different sets are two runs
    /// nobody can compare, and nothing downstream would report the difference.
    /// </remarks>
    double Rank(in CoSimVehicleFrame frame, bool alreadyRendered);
}
