namespace CarlaNet.CoSim;

/// <summary>
/// One CARLA blueprint's measured body, and the offsets a pose conversion needs from it.
/// </summary>
/// <param name="BlueprintId">The CARLA blueprint the measurement was taken from.</param>
/// <param name="LengthMetres">Twice the measured bounding-box extent along the body's own forward axis.</param>
/// <param name="WidthMetres">Twice the measured extent across it.</param>
/// <param name="HeightMetres">Twice the measured extent up it.</param>
/// <param name="BoxCentreMetres">
/// Where the measured box's centre sits in the actor's own frame. Not zero: the mesh of a body is
/// not in general centred on the origin CARLA places the actor at.
/// </param>
public readonly record struct VehicleExtent(
    string BlueprintId,
    double LengthMetres,
    double WidthMetres,
    double HeightMetres,
    (double X, double Y, double Z) BoxCentreMetres)
{
    /// <summary>
    /// Distance from the centre of the front bumper back along the body's own heading to the actor
    /// origin.
    /// </summary>
    /// <remarks>
    /// Half the body's length reaches the box centre; the box centre's own forward offset reaches
    /// the origin under it. This is the shift that puts the rendered front bumper exactly where
    /// SUMO says the vehicle's reference point is, and it is not half the vType's declared length:
    /// that number is what a scenario author wrote, not a measurement of the body that will be
    /// drawn.
    /// </remarks>
    public double BumperToOriginMetres => (LengthMetres / 2.0) + BoxCentreMetres.X;

    /// <summary>
    /// Sideways offset of the box centre from the actor origin, in the body's own frame.
    /// </summary>
    /// <remarks>
    /// Measured at or near zero for most of the shipped content and at -0.0925 m for the Mitsubishi
    /// Fuso, which is why it is applied rather than assumed away.
    /// </remarks>
    public double LateralOffsetMetres => BoxCentreMetres.Y;

    /// <summary>
    /// Signed height of the actor origin above the lowest point of the measured box: negative where
    /// the box bottom sits above the origin, which is how every blueprint in the shipped catalogue
    /// measures.
    /// </summary>
    /// <remarks>
    /// <b>An approximation, and a pose records that it used one.</b> What the seating actually needs
    /// is the height of the actor origin above the surface the wheels rest on, which is a property
    /// of the collision body rather than of the visual bounding box and is established by spawning
    /// the blueprint on level ground and letting it settle. The catalogue carries no such
    /// measurement, so this stands in for it: the two differ by however far the box bottom sits from
    /// the contact patch. Measured across the shipped catalogue that gap is a centimetre or less --
    /// 7 mm for the Dodge Charger, 11 mm for the Mitsubishi Fuso -- which says the measured boxes
    /// enclose the wheels.
    /// </remarks>
    public double ApproximateSeatHeightMetres => (HeightMetres / 2.0) - BoxCentreMetres.Z;
}
