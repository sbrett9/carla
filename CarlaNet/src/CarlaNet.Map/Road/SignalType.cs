// Source: carla/road/SignalType.h + SignalType.cpp
//
// Maps OpenDRIVE 1.5M country-code signal type strings to semantic categories
// (traffic light vs. stop sign vs. yield, etc.). Held as constant strings to
// keep parity with upstream (which exposes them as static getters).
namespace CarlaNet.Map.Road;

public static class SignalType
{
    public const string Danger = "101";                          // danger types 101..151
    public const string LanesMerging = "121";
    public const string CautionPedestrian = "133";
    public const string CautionBicycle = "138";
    public const string LevelCrossing = "150";
    public const string YieldSign = "205";
    public const string StopSign = "206";
    public const string MandatoryTurnDirection = "209";          // left/right/forward
    public const string MandatoryLeftRightDirection = "211";
    public const string TwoChoiceTurnDirection = "214";          // forward-left/forward-right/left-right
    public const string Roundabout = "215";
    public const string PassRightLeft = "222";
    public const string AccessForbidden = "250";
    public const string AccessForbiddenMotorvehicles = "251";
    public const string AccessForbiddenTrucks = "253";
    public const string AccessForbiddenBicycle = "254";
    public const string AccessForbiddenWeight = "263";
    public const string AccessForbiddenWidth = "264";
    public const string AccessForbiddenHeight = "265";
    public const string AccessForbiddenWrongDirection = "267";
    public const string ForbiddenUTurn = "272";
    public const string MaximumSpeed = "274";
    public const string ForbiddenOvertakingMotorvehicles = "276";
    public const string ForbiddenOvertakingTrucks = "277";
    public const string AbsoluteNoStop = "283";
    public const string RestrictedStop = "286";
    public const string HasWayNextIntersection = "301";
    public const string PriorityWay = "306";
    public const string PriorityWayEnd = "307";
    public const string CityBegin = "310";
    public const string CityEnd = "311";
    public const string Highway = "330";
    public const string DeadEnd = "357";
    public const string RecomendedSpeed = "380";
    public const string RecomendedSpeedEnd = "381";

    // Upstream: SignalType::IsTrafficLight. RoadRunner emits these 1000xxx codes
    // for traffic-light variants; "F","W","A" are legacy fallbacks.
    private static readonly HashSet<string> _trafficLightTypes = new()
    {
        "1000001", "1000002", "1000009", "1000010", "1000011",
        "1000007", "1000014", "1000015", "1000016", "1000017",
        "1000018", "1000019", "1000013", "1000020", "1000008",
        "1000012", "F", "W", "A",
    };

    public static bool IsTrafficLight(string type) => _trafficLightTypes.Contains(type);
}
