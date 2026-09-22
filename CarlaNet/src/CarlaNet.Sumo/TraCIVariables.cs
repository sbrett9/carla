namespace CarlaNet.Sumo;

/// <summary>
/// The vehicle variables a co-simulation bridge subscribes to, and the ones no generic decoder can
/// read.
/// </summary>
/// <remarks>
/// <para><b>What is subscribed is a cost, not a preference.</b> SUMO fills a subscription's results
/// while it advances, so the charge lands inside the step whether or not anyone reads them:
/// measured at 388 live vehicles on the Arapahoe network, 3.73 ms per step with nothing subscribed
/// against 9.26 ms with the set below subscribed and never read. Adding a variable here adds to
/// every step of every run.</para>
/// </remarks>
public static class TraCIVariables
{
    /// <summary>
    /// A domain's subscribe command is its get command plus this, which is how a variable
    /// identifier is resolved to the domain it belongs to.
    /// </summary>
    internal const int SubscribeOffset = 0x30;

    /// <summary>Position of the centre of the front bumper, in projected metres.</summary>
    public const int Position = TraCIConstants.VAR_POSITION;

    /// <summary>Heading in degrees clockwise from north.</summary>
    public const int Angle = TraCIConstants.VAR_ANGLE;

    /// <summary>Speed along the lane, in metres per second.</summary>
    public const int Speed = TraCIConstants.VAR_SPEED;

    /// <summary>The edge the vehicle is on. A junction-internal edge is named with a leading colon.</summary>
    public const int RoadId = TraCIConstants.VAR_ROAD_ID;

    /// <summary>The lane the vehicle is on, as <c>edge_index</c>.</summary>
    public const int LaneId = TraCIConstants.VAR_LANE_ID;

    /// <summary>
    /// How far along its lane the vehicle's front bumper is, in metres from the lane's start.
    /// </summary>
    /// <remarks>
    /// Not in <see cref="VehicleState"/>, because nothing that reads a vehicle's state needs it and
    /// every subscribed variable is charged to every step. A bridge that interpolates between two
    /// SUMO frames along the lane's own polyline does need it -- it is the parameter the polyline is
    /// evaluated at -- and subscribes it on top of the set below.
    /// </remarks>
    public const int LanePosition = TraCIConstants.VAR_LANEPOSITION;

    /// <summary>The vehicle type identifier, which a catalogue maps to a renderable blueprint.</summary>
    public const int TypeId = TraCIConstants.VAR_TYPE;

    /// <summary>The signal word: indicators and brake light. See <see cref="SumoVehicleSignals"/>.</summary>
    public const int Signals = TraCIConstants.VAR_SIGNALS;

    /// <summary>
    /// The set the bridge reads: pose, motion, where on the network, what kind of vehicle, and what
    /// its lights are doing. Everything <see cref="SumoVehicleState"/> carries and nothing else.
    /// </summary>
    public static readonly int[] VehicleState =
        [Position, Angle, Speed, RoadId, LaneId, TypeId, Signals];

    /// <summary>
    /// Variables whose value SUMO writes as a compound of untyped fields, by the domain they belong
    /// to.
    /// </summary>
    /// <remarks>
    /// <para>Every entry is one SUMO's own client gives a dedicated parser to, in that domain's
    /// <c>_RETURN_VALUE_FUNC</c>. Their members carry no type bytes -- the best-lanes list, for
    /// instance, writes a raw count and then raw doubles -- so reading one generically does not
    /// fail, it silently consumes the wrong number of bytes and mis-decodes everything after it in
    /// the frame. Subscribing to one is refused up front for that reason.</para>
    ///
    /// <para><b>Keyed by domain, because a variable identifier only means something inside one.</b>
    /// <c>0x74</c> is the vehicle domain's follower, which needs a decoder, and the simulation
    /// domain's list of vehicles that departed this step, which is an ordinary string list. A flat
    /// set of numbers would refuse the second on account of the first.</para>
    /// </remarks>
    private static readonly Dictionary<int, HashSet<int>> NeedDedicatedDecoders = new()
    {
        [TraCIConstants.CMD_GET_VEHICLE_VARIABLE] =
        [
            TraCIConstants.VAR_BEST_LANES,
            TraCIConstants.VAR_LEADER,
            TraCIConstants.VAR_FOLLOWER,
            TraCIConstants.VAR_NEIGHBORS,
            TraCIConstants.VAR_NEXT_TLS,
            TraCIConstants.VAR_NEXT_STOPS,
            TraCIConstants.VAR_NEXT_STOPS2,
            TraCIConstants.VAR_NEXT_LINKS,
            TraCIConstants.VAR_FOES,
            TraCIConstants.VAR_ROUTE_VALID,
            TraCIConstants.CMD_CHANGELANE,
        ],
        [TraCIConstants.CMD_GET_SIM_VARIABLE] =
        [
            TraCIConstants.FIND_ROUTE,
            TraCIConstants.VAR_COLLISIONS,
        ],
        [TraCIConstants.CMD_GET_TL_VARIABLE] =
        [
            TraCIConstants.TL_COMPLETE_DEFINITION_RYG,
            TraCIConstants.TL_CONTROLLED_LINKS,
            TraCIConstants.TL_CONSTRAINT,
            TraCIConstants.TL_CONSTRAINT_BYFOE,
            TraCIConstants.TL_CONSTRAINT_SWAP,
        ],
        [TraCIConstants.CMD_GET_LANE_VARIABLE] = [TraCIConstants.LANE_LINKS],
        [TraCIConstants.CMD_GET_PERSON_VARIABLE] =
        [
            TraCIConstants.VAR_STAGE,
            TraCIConstants.VAR_TAXI_RESERVATIONS,
        ],
        [TraCIConstants.CMD_GET_INDUCTIONLOOP_VARIABLE] = [TraCIConstants.LAST_STEP_VEHICLE_DATA],
        [TraCIConstants.CMD_GET_POLYGON_VARIABLE] = [TraCIConstants.VAR_FILL],
        [TraCIConstants.CMD_GET_GUI_VARIABLE] =
        [
            TraCIConstants.VAR_HAS_VIEW,
            TraCIConstants.VAR_SELECT,
        ],
    };

    /// <summary>
    /// Whether this client can decode the named variable of the named domain from its type byte
    /// alone.
    /// </summary>
    /// <param name="getCommandId">The domain's get command, such as
    /// <see cref="TraCIConstants.CMD_GET_VEHICLE_VARIABLE"/>.</param>
    /// <param name="variableId">The variable within that domain.</param>
    public static bool IsDecodable(int getCommandId, int variableId) =>
        !NeedDedicatedDecoders.TryGetValue(getCommandId, out HashSet<int>? refused)
        || !refused.Contains(variableId);

    /// <summary>
    /// Refuse a variable this client cannot decode, before a subscription to it is ever made.
    /// </summary>
    /// <param name="getCommandId">The domain's get command.</param>
    /// <param name="variableId">The variable within that domain.</param>
    /// <exception cref="ArgumentException">The variable needs a decoder written for it.</exception>
    public static void EnsureDecodable(int getCommandId, int variableId)
    {
        if (!IsDecodable(getCommandId, variableId))
        {
            throw new ArgumentException(
                $"TraCI variable 0x{variableId:x2} of domain 0x{getCommandId:x2} is written as a "
                + "compound of untyped fields, which this client has no decoder for. Refused here "
                + "rather than at the point of reading, because a value of that shape read "
                + "generically consumes the wrong number of bytes and mis-decodes the rest of the "
                + "frame instead of failing.",
                nameof(variableId));
        }
    }
}
