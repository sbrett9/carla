namespace CarlaNet.CoSim;

/// <summary>
/// A SUMO vehicle type the catalogue cannot supply a rendered extent for.
/// </summary>
/// <remarks>
/// <para><b>Such a vehicle is not rendered, and is never placed at a guess.</b> Falling back to the
/// vType's declared length would put a body of unknown size at a pose computed from a number nobody
/// measured, and the error is undetectable afterwards: the imagery and the truth record would agree
/// with each other while both were wrong.</para>
///
/// <para>The reason is carried as its own value so a caller can record the vehicle as simulated but
/// not rendered without re-deriving the reason from the message.</para>
/// </remarks>
public sealed class UnrenderableVehicleTypeException : Exception
{
    public UnrenderableVehicleTypeException(string vehicleTypeId,
                                            UnrenderableReason reason,
                                            string detail)
        : base($"vehicle type '{vehicleTypeId}' cannot be rendered ({reason}): {detail}")
    {
        VehicleTypeId = vehicleTypeId;
        Reason = reason;
        Detail = detail;
    }

    /// <summary>The SUMO vType the scenario declared.</summary>
    public string VehicleTypeId { get; }

    /// <summary>Which of the refusals this is.</summary>
    public UnrenderableReason Reason { get; }

    /// <summary>What was looked for and what was found.</summary>
    public string Detail { get; }
}
