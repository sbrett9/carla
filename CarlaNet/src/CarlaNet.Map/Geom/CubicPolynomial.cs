// Source: carla/geom/CubicPolynomial.h
// f(x) = a + bx + cx^2 + dx^3
// Used by Poly3 / ParamPoly3 geometries and by elevation / lane-offset profiles.
namespace CarlaNet.Map.Geom;

public sealed class CubicPolynomial
{
    private double _a;
    private double _b;
    private double _c;
    private double _d;
    private double _s;

    public CubicPolynomial() { }

    public CubicPolynomial(double a, double b, double c, double d)
    {
        _a = a; _b = b; _c = c; _d = d; _s = 0.0;
    }

    public CubicPolynomial(double a, double b, double c, double d, double s)
    {
        Set(a, b, c, d, s);
    }

    public double GetA() => _a;
    public double GetB() => _b;
    public double GetC() => _c;
    public double GetD() => _d;
    public double GetS() => _s;

    public void Set(double a, double b, double c, double d)
    {
        _a = a; _b = b; _c = c; _d = d; _s = 0.0;
    }

    // lateral offset variant: shift the polynomial by s so it evaluates at (x - s).
    public void Set(double a, double b, double c, double d, double s)
    {
        _a = a - b * s + c * s * s - d * s * s * s;
        _b = b - 2 * c * s + 3 * d * s * s;
        _c = c - 3 * d * s;
        _d = d;
        _s = s;
    }

    /// f(x) = a + bx + cx^2 + dx^3 (Horner form)
    public double Evaluate(double x) => _a + x * (_b + x * (_c + x * _d));

    /// df/dx = b + 2cx + 3dx^2
    public double Tangent(double x) => _b + x * (2 * _c + x * 3 * _d);

    public static CubicPolynomial operator +(CubicPolynomial lhs, CubicPolynomial rhs) =>
        new(lhs._a + rhs._a, lhs._b + rhs._b, lhs._c + rhs._c, lhs._d + rhs._d);

    public static CubicPolynomial operator *(CubicPolynomial lhs, double rhs) =>
        new(lhs._a * rhs, lhs._b * rhs, lhs._c * rhs, lhs._d * rhs);

    public static CubicPolynomial operator *(double lhs, CubicPolynomial rhs) => rhs * lhs;
}
