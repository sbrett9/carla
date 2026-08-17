// Checks RotationBasis against carla/geom/Math.cpp and carla/geom/Rotation.h: the axes for hand-
// checkable rotations, and the identities that tie the axes to the point rotations.
using CarlaNet.Types.Geom;

namespace CarlaNet.Tests.Geom;

public class RotationBasisTests
{
    private const double Tolerance = 1e-5;

    private static void AssertVector(double x, double y, double z, Vector3D actual)
    {
        Assert.Equal(x, actual.X, Tolerance);
        Assert.Equal(y, actual.Y, Tolerance);
        Assert.Equal(z, actual.Z, Tolerance);
    }

    [Fact]
    public void Identity_Rotation_Gives_The_Simulator_Axes()
    {
        var basis = new RotationBasis(new Rotation(0f, 0f, 0f));
        AssertVector(1, 0, 0, basis.Forward);
        AssertVector(0, 1, 0, basis.Right);
        AssertVector(0, 0, 1, basis.Up);
    }

    [Fact]
    public void Yaw_Turns_Forward_Onto_The_Y_Axis()
    {
        var basis = new RotationBasis(new Rotation(0f, 90f, 0f));
        AssertVector(0, 1, 0, basis.Forward);
        AssertVector(-1, 0, 0, basis.Right);
        AssertVector(0, 0, 1, basis.Up);
    }

    [Fact]
    public void Pitch_Up_Points_Forward_At_The_Sky()
    {
        var basis = new RotationBasis(new Rotation(90f, 0f, 0f));
        AssertVector(0, 0, 1, basis.Forward);
        AssertVector(0, 1, 0, basis.Right);
        AssertVector(-1, 0, 0, basis.Up);
    }

    [Fact]
    public void Roll_Rotates_Right_And_Up_About_Forward()
    {
        var basis = new RotationBasis(new Rotation(0f, 0f, 90f));
        AssertVector(1, 0, 0, basis.Forward);
        AssertVector(0, 0, -1, basis.Right);
        AssertVector(0, 1, 0, basis.Up);
    }

    [Fact]
    public void Rotate_Maps_The_Unit_Vectors_Onto_The_Axes()
    {
        var rotation = new Rotation(-23f, 137f, 41f);
        var basis = new RotationBasis(rotation);
        AssertVector(basis.Forward.X, basis.Forward.Y, basis.Forward.Z, basis.Rotate(new Vector3D(1, 0, 0)));
        AssertVector(basis.Right.X, basis.Right.Y, basis.Right.Z, basis.Rotate(new Vector3D(0, 1, 0)));
        AssertVector(basis.Up.X, basis.Up.Y, basis.Up.Z, basis.Rotate(new Vector3D(0, 0, 1)));
    }

    [Fact]
    public void InverseRotate_Undoes_Rotate()
    {
        var basis = new RotationBasis(new Rotation(-23f, 137f, 41f));
        var point = new Vector3D(3.5f, -1.25f, 8f);
        AssertVector(point.X, point.Y, point.Z, basis.InverseRotate(basis.Rotate(point)));
    }

    [Fact]
    public void Axes_Are_Orthonormal()
    {
        var basis = new RotationBasis(new Rotation(-23f, 137f, 41f));
        static double Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        Assert.Equal(1.0, Dot(basis.Forward, basis.Forward), Tolerance);
        Assert.Equal(1.0, Dot(basis.Right, basis.Right), Tolerance);
        Assert.Equal(1.0, Dot(basis.Up, basis.Up), Tolerance);
        Assert.Equal(0.0, Dot(basis.Forward, basis.Right), Tolerance);
        Assert.Equal(0.0, Dot(basis.Forward, basis.Up), Tolerance);
        Assert.Equal(0.0, Dot(basis.Right, basis.Up), Tolerance);
    }
}
