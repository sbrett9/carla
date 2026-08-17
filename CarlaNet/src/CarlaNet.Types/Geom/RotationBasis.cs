// Source: carla/geom/Math.cpp (GetForwardVector / GetRightVector / GetUpVector) and
// carla/geom/Rotation.h (RotateVector / InverseRotateVector). Both are the same rotation matrix
// Rz(yaw)*Ry(pitch)*Rx(roll), whose columns ARE the forward/right/up axes, so one cached basis
// reproduces the axis accessors and the point rotations alike.
namespace CarlaNet.Types.Geom;

/// <summary>
/// The three orthonormal axes of a <see cref="Rotation"/> — forward (local +X), right (local +Y) and
/// up (local +Z) — expressed in the frame that rotation is relative to. Evaluating the six
/// trigonometric terms once and reusing the axes reduces every later point rotation to three
/// multiply-adds, which is what makes transforming thousands of camera rays per frame affordable.
/// </summary>
public readonly struct RotationBasis
{
    public Vector3D Forward { get; }
    public Vector3D Right { get; }
    public Vector3D Up { get; }

    public RotationBasis(Rotation rotation)
    {
        const float DegToRad = MathF.PI / 180.0f;
        float cp = MathF.Cos(rotation.Pitch * DegToRad), sp = MathF.Sin(rotation.Pitch * DegToRad);
        float cy = MathF.Cos(rotation.Yaw * DegToRad), sy = MathF.Sin(rotation.Yaw * DegToRad);
        float cr = MathF.Cos(rotation.Roll * DegToRad), sr = MathF.Sin(rotation.Roll * DegToRad);
        Forward = new Vector3D(cp * cy, cp * sy, sp);
        Right = new Vector3D(cy * sp * sr - sy * cr, sy * sp * sr + cy * cr, -cp * sr);
        Up = new Vector3D(-cy * sp * cr - sy * sr, -sy * sp * cr + cy * sr, cp * cr);
    }

    /// <summary>Rotate a vector from the local frame into the parent frame — the same result as
    /// carla::geom::Rotation::RotateVector.</summary>
    public Vector3D Rotate(Vector3D local) => new(
        Forward.X * local.X + Right.X * local.Y + Up.X * local.Z,
        Forward.Y * local.X + Right.Y * local.Y + Up.Y * local.Z,
        Forward.Z * local.X + Right.Z * local.Y + Up.Z * local.Z);

    /// <summary>Rotate a vector from the parent frame back into the local frame — the transpose of
    /// <see cref="Rotate"/>, i.e. carla::geom::Rotation::InverseRotateVector.</summary>
    public Vector3D InverseRotate(Vector3D parent) => new(
        parent.X * Forward.X + parent.Y * Forward.Y + parent.Z * Forward.Z,
        parent.X * Right.X + parent.Y * Right.Y + parent.Z * Right.Z,
        parent.X * Up.X + parent.Y * Up.Y + parent.Z * Up.Z);
}
