// Source: third-party/odrSpiral/odrSpiral.cpp (VIRES, Apache 2.0).
// Rational approximations from the CEPHES library (http://www.netlib.org/cephes/)
// for the Fresnel integrals S(x) = integral_0^x sin(pi/2 t^2) dt and C(x) likewise.
// All math is in double precision to match upstream — Risk #1 in the port spec
// (spiral waypoints drift tens of cm if narrowed to float).
namespace CarlaNet.Map.Geom;

internal static class Fresnel
{
    // S(x) numerator for small x.
    private static readonly double[] Sn =
    {
        -2.99181919401019853726e3,
         7.08840045257738576863e5,
        -6.29741486205862506537e7,
         2.54890880573376359104e9,
        -4.42979518059697779103e10,
         3.18016297876567817986e11,
    };

    // S(x) denominator for small x (leading 1.0 omitted; used with p1evl).
    private static readonly double[] Sd =
    {
         2.81376268889994315696e2,
         4.55847810806532581675e4,
         5.17343888770096400730e6,
         4.19320245898111231129e8,
         2.24411795645340920940e10,
         6.07366389490084639049e11,
    };

    // C(x) numerator for small x.
    private static readonly double[] Cn =
    {
        -4.98843114573573548651e-8,
         9.50428062829859605134e-6,
        -6.45191435683965050962e-4,
         1.88843319396703850064e-2,
        -2.05525900955013891793e-1,
         9.99999999999999998822e-1,
    };

    // C(x) denominator for small x (uses polevl — leading coefficient kept).
    private static readonly double[] Cd =
    {
         3.99982968972495980367e-12,
         9.15439215774657478799e-10,
         1.25001862479598821474e-7,
         1.22262789024179030997e-5,
         8.68029542941784300606e-4,
         4.12142090722199792936e-2,
         1.00000000000000000118e0,
    };

    // Auxiliary f(x) numerator (large x).
    private static readonly double[] Fn =
    {
        4.21543555043677546506e-1,
        1.43407919780758885261e-1,
        1.15220955073585758835e-2,
        3.45017939782574027900e-4,
        4.63613749287867322088e-6,
        3.05568983790257605827e-8,
        1.02304514164907233465e-10,
        1.72010743268161828879e-13,
        1.34283276233062758925e-16,
        3.76329711269987889006e-20,
    };

    // Auxiliary f(x) denominator (large x, leading 1.0 omitted).
    private static readonly double[] Fd =
    {
        7.51586398353378947175e-1,
        1.16888925859191382142e-1,
        6.44051526508858611005e-3,
        1.55934409164153020873e-4,
        1.84627567348930545870e-6,
        1.12699224763999035261e-8,
        3.60140029589371370404e-11,
        5.88754533621578410010e-14,
        4.52001434074129701496e-17,
        1.25443237090011264384e-20,
    };

    // Auxiliary g(x) numerator (large x).
    private static readonly double[] Gn =
    {
        5.04442073643383265887e-1,
        1.97102833525523411709e-1,
        1.87648584092575249293e-2,
        6.84079380915393090172e-4,
        1.15138826111884280931e-5,
        9.82852443688422223854e-8,
        4.45344415861750144738e-10,
        1.08268041139020870318e-12,
        1.37555460633261799868e-15,
        8.36354435630677421531e-19,
        1.86958710162783235106e-22,
    };

    // Auxiliary g(x) denominator (large x, leading 1.0 omitted).
    private static readonly double[] Gd =
    {
        1.47495759925128324529e0,
        3.37748989120019970451e-1,
        2.53603741420338795122e-2,
        8.14679107184306179049e-4,
        1.27545075667729118702e-5,
        1.04314589657571990585e-7,
        4.60680728146520428211e-10,
        1.10273215066240270757e-12,
        1.38796531259578871258e-15,
        8.39158816283118707363e-19,
        1.86958710162783236342e-22,
    };

    // Polynomial evaluation: ans = coef[0]*x^n + coef[1]*x^(n-1) + ... + coef[n].
    private static double Polevl(double x, double[] coef, int n)
    {
        double ans = coef[0];
        for (var i = 1; i <= n; i++)
        {
            ans = ans * x + coef[i];
        }
        return ans;
    }

    // Polynomial evaluation with implied leading coefficient of 1:
    // ans = x^n + coef[0]*x^(n-1) + ... + coef[n-1].
    private static double P1evl(double x, double[] coef, int n)
    {
        double ans = x + coef[0];
        for (var i = 1; i < n; i++)
        {
            ans = ans * x + coef[i];
        }
        return ans;
    }

    /// Computes the Fresnel integrals: ssa = S(xxa), cca = C(xxa).
    public static void Compute(double xxa, out double ssa, out double cca)
    {
        var x = Math.Abs(xxa);
        var x2 = x * x;
        double cc, ss;

        if (x2 < 2.5625)
        {
            var t = x2 * x2;
            ss = x * x2 * Polevl(t, Sn, 5) / P1evl(t, Sd, 6);
            cc = x * Polevl(t, Cn, 5) / Polevl(t, Cd, 6);
        }
        else if (x > 36974.0)
        {
            cc = 0.5;
            ss = 0.5;
        }
        else
        {
            x2 = x * x;
            var t = Math.PI * x2;
            var u = 1.0 / (t * t);
            t = 1.0 / t;
            var f = 1.0 - u * Polevl(u, Fn, 9) / P1evl(u, Fd, 10);
            var g = t * Polevl(u, Gn, 10) / P1evl(u, Gd, 11);

            t = Math.PI * 0.5 * x2;
            var c = Math.Cos(t);
            var s = Math.Sin(t);
            t = Math.PI * x;
            cc = 0.5 + (f * s - g * c) / t;
            ss = 0.5 - (f * c + g * s) / t;
        }

        if (xxa < 0.0)
        {
            cc = -cc;
            ss = -ss;
        }

        cca = cc;
        ssa = ss;
    }

    /// Compute the OpenDRIVE "standard" spiral starting with curvature 0.
    /// s    — run-length along spiral [m]
    /// cDot — first derivative of curvature [1/m^2]
    /// outputs: (x, y) in spiral local coords; t tangent direction [rad]
    public static void OdrSpiral(double s, double cDot, out double x, out double y, out double t)
    {
        var a = 1.0 / Math.Sqrt(Math.Abs(cDot));
        a *= Math.Sqrt(Math.PI);

        Compute(s / a, out y, out x);

        x *= a;
        y *= a;

        if (cDot < 0.0)
        {
            y = -y;
        }

        t = s * s * cDot * 0.5;
    }
}
