using CarlaNet.Types.Geom;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// What the bridge told one body to be at one tick, against what the world says it became.
/// </summary>
/// <param name="TickIndex">Ticks since the session's first, counting from zero.</param>
/// <param name="SimulatedTimeSeconds">The instant the pose was computed for.</param>
/// <param name="VehicleId">The SUMO vehicle the pose belongs to.</param>
/// <param name="Actor">The body it was written to.</param>
/// <param name="Commanded">The pose the conversion produced.</param>
/// <param name="Observed">The transform the world observer reported for that body.</param>
/// <param name="PositionMetres">Straight-line separation between the two positions.</param>
/// <param name="YawDegrees">Shortest-arc separation in yaw.</param>
/// <param name="PitchDegrees">Shortest-arc separation in pitch.</param>
/// <param name="RollDegrees">Shortest-arc separation in roll.</param>
/// <remarks>
/// <para><b>This is the plan's own check on the three pose conversions, and it is free.</b> The
/// world observer streams every actor's transform every tick whether or not anything reads it, so a
/// comparison against the pose that was just written costs an arithmetic subtraction and no round
/// trip. What it turns into a measurement is the whole class of failure this mode is exposed to: a
/// frame, a yaw or a reference point that is wrong produces imagery full of plausible cars in
/// plausible places with every truth box in the wrong place, and no amount of looking at the imagery
/// finds it.</para>
///
/// <para><b>What a residual means.</b> Near zero: the world did what it was told, and whether it was
/// told the right thing is the bumper and lane residuals' question, not this one. A residual of
/// about one tick's travel on every moving vehicle and none on a stationary one: the read-back is
/// lagging the write by a frame, which is an ordering fault in the bridge, not a conversion error. A
/// residual of a hundred times the pose: a unit is metres at one end and centimetres at the other. A
/// residual on one vehicle and not the rest: that body did not take the write, which the batch's own
/// refusal count should also show.</para>
/// </remarks>
public readonly record struct PoseDivergence(
    long TickIndex,
    double SimulatedTimeSeconds,
    string VehicleId,
    ActorId Actor,
    VehiclePose Commanded,
    Transform Observed,
    double PositionMetres,
    double YawDegrees,
    double PitchDegrees,
    double RollDegrees)
{
    /// <summary>
    /// The comparison of one commanded pose against one observed transform.
    /// </summary>
    public static PoseDivergence Between(long tickIndex,
                                         double simulatedTimeSeconds,
                                         string vehicleId,
                                         ActorId actor,
                                         in VehiclePose commanded,
                                         in Transform observed)
    {
        double dx = observed.Location.X - commanded.X;
        double dy = observed.Location.Y - commanded.Y;
        double dz = observed.Location.Z - commanded.Z;
        return new PoseDivergence(
            tickIndex,
            simulatedTimeSeconds,
            vehicleId,
            actor,
            commanded,
            observed,
            Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)),
            Separation(observed.Rotation.Yaw, commanded.YawDegrees),
            Separation(observed.Rotation.Pitch, commanded.PitchDegrees),
            Separation(observed.Rotation.Roll, commanded.RollDegrees));
    }

    /// <summary>
    /// The shortest arc between two angles in degrees, unsigned.
    /// </summary>
    /// <remarks>
    /// Shortest arc rather than a subtraction, because a body commanded to 179 degrees and reported
    /// at -179 has turned two degrees and not three hundred and fifty-eight. The engine's own
    /// quaternion-to-rotator conversion is what puts the two on opposite sides of the wrap.
    /// </remarks>
    public static double Separation(double left, double right)
    {
        double difference = Math.IEEERemainder(left - right, 360.0);
        return Math.Abs(difference);
    }
}
