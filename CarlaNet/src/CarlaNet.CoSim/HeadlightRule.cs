using System.Globalization;
using CarlaNet.Types.Rpc.Lighting;

namespace CarlaNet.CoSim;

/// <summary>
/// Whether every rendered vehicle has its headlights on, from the elevation of the sun the world
/// reports, with a band between switching on and switching off.
/// </summary>
/// <remarks>
/// <para><b>From the sun, because nothing else can supply them.</b> SUMO declares front, fog and high-beam
/// signal bits and never writes any of them, so a headlight is not something the simulation knows. What
/// is known is the sun, and the rule is the time-and-illumination plan's (D11.9):
/// <c>Position | LowBeam</c> asserted once the sun's elevation falls below
/// <see cref="OnBelowDegrees"/> and cleared once it rises above <see cref="OffAboveDegrees"/>, +3 and +6
/// degrees by default. At the sizing site the sun takes about fourteen minutes to cross that band, so a
/// window sees at most one switch and no chatter.</para>
///
/// <para><b>The elevation is the one the world reports</b> -- the geometric elevation it publishes with
/// each snapshot, the <c>sun_elevation_deg</c> a frame's solar record carries -- so the lamps in the imagery
/// and the sun in the record come from one source. Under a frozen sun it is the same value for every
/// frame and the rule is decided once. Under an advancing sun a tick's lamps are written before its frame
/// renders, so they follow the sun the previous frame reported, a twentieth of a second of sun earlier.</para>
///
/// <para><b>It is the same for every vehicle.</b> It carries nothing about any one of them, so it cannot
/// become a label; a vehicle's lamps differ from its neighbours' only where SUMO said its brakes or
/// indicators were on.</para>
///
/// <para><b>Inside the band at the start</b> -- a window opening between the two thresholds -- the rule
/// starts with the lamps off, as though the sun had come down into the band: the session has no history
/// to say which way it came.</para>
/// </remarks>
public sealed class HeadlightRule
{
    /// <summary>The default elevation, degrees, below which headlights come on.</summary>
    public const double DefaultOnBelowDegrees = 3.0;

    /// <summary>The default elevation, degrees, above which headlights go off.</summary>
    public const double DefaultOffAboveDegrees = 6.0;

    /// <summary>The lamps a headlight switched on lights.</summary>
    public const VehicleLightStateFlags Headlights = VehicleLightStateFlags.Position | VehicleLightStateFlags.LowBeam;

    private bool _started;

    /// <param name="onBelowDegrees">The sun's elevation, degrees, below which headlights come on.</param>
    /// <param name="offAboveDegrees">The elevation above which they go off; above the first.</param>
    /// <exception cref="CoSimSessionRefusedException">
    /// Either is not a number, or the band between them is empty or inverted, which would switch the
    /// lamps on every frame the sun sat in it.
    /// </exception>
    public HeadlightRule(double onBelowDegrees, double offAboveDegrees)
    {
        if (!double.IsFinite(onBelowDegrees) || !double.IsFinite(offAboveDegrees)
            || offAboveDegrees <= onBelowDegrees)
        {
            throw new CoSimSessionRefusedException(
                $"Headlights are to come on below {Degrees(onBelowDegrees)} degrees of sun and go off above "
                + $"{Degrees(offAboveDegrees)}. The second has to be a number above the first: the band "
                + "between them is what keeps a sun sitting near one threshold from switching every "
                + "vehicle's lamps frame by frame.");
        }

        OnBelowDegrees = onBelowDegrees;
        OffAboveDegrees = offAboveDegrees;
    }

    /// <summary>The elevation, degrees, below which headlights come on.</summary>
    public double OnBelowDegrees { get; }

    /// <summary>The elevation, degrees, above which headlights go off.</summary>
    public double OffAboveDegrees { get; }

    /// <summary>Whether headlights are on now.</summary>
    public bool On { get; private set; }

    /// <summary>Whether headlights were on at the first elevation the rule was given.</summary>
    public bool? OnAtStart { get; private set; }

    /// <summary>How many times they have switched since.</summary>
    public int Switches { get; private set; }

    /// <summary>The lamps the rule asserts now.</summary>
    public VehicleLightStateFlags Lamps => On ? Headlights : VehicleLightStateFlags.None;

    /// <summary>Take the sun's elevation and answer the lamps it asserts.</summary>
    /// <param name="elevationDegrees">The geometric elevation the world reported, degrees.</param>
    public VehicleLightStateFlags Update(double elevationDegrees)
    {
        if (!_started)
        {
            _started = true;
            On = elevationDegrees < OnBelowDegrees;
            OnAtStart = On;
            return Lamps;
        }

        bool on = On ? elevationDegrees <= OffAboveDegrees : elevationDegrees < OnBelowDegrees;
        if (on != On)
        {
            On = on;
            Switches++;
        }

        return Lamps;
    }

    /// <summary>The rule and what it has done, in the report's words.</summary>
    public override string ToString() =>
        $"headlights on below {Degrees(OnBelowDegrees)} deg of sun, off above {Degrees(OffAboveDegrees)} deg"
        + (OnAtStart is { } start
            ? $"; {(start ? "on" : "off")} at the first frame, {Switches} switch(es) since"
            : "; no sun reported yet");

    private static string Degrees(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
