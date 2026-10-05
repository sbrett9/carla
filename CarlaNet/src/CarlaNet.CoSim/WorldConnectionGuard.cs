using System.Net.Sockets;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Commands;
using CarlaNet.Types.Rpc.Environment;

using ActorId = uint;

namespace CarlaNet.CoSim;

/// <summary>
/// The world a session drives, with a failure of the connection to it told apart from every other
/// failure on the way through.
/// </summary>
/// <remarks>
/// <para>A session reads files as well as talking to a server, and an <see cref="IOException"/> from a
/// world package that cannot be opened is not a dropped connection. So the distinction is made here, at
/// the one place every call to the world passes, rather than by the type of whatever reaches the
/// session: a call that fails because the connection failed -- the socket closed or reset
/// (<see cref="IOException"/>, <see cref="SocketException"/>), or no answer came within the client's
/// timeout (<see cref="TimeoutException"/>) -- raises <see cref="WorldConnectionLostException"/> with
/// that failure inside it, and anything else passes through as it was.</para>
///
/// <para>The CARLA client raises exactly these: a write to a socket the server closed is an
/// <see cref="IOException"/>, a read that fails is one too, and a call whose answer never comes -- a
/// tick cue included -- is a <see cref="TimeoutException"/> once the client's per-call timeout has
/// passed. A server that answers with an error is not a connection failure and passes through.</para>
/// </remarks>
internal sealed class WorldConnectionGuard : ICarlaWorld
{
    private readonly ICarlaWorld _world;

    public WorldConnectionGuard(ICarlaWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
    }

    /// <inheritdoc/>
    public LoadedWorld DescribeLoadedWorld() => Guard(nameof(DescribeLoadedWorld), _world.DescribeLoadedWorld);

    /// <inheritdoc/>
    public bool AdoptBareEarthGrids(string packagePath) =>
        Guard(nameof(AdoptBareEarthGrids), () => _world.AdoptBareEarthGrids(packagePath));

    /// <inheritdoc/>
    public void AdoptCatalogueSpecialTypes(IReadOnlyDictionary<string, string> specialTypes) =>
        Guard(nameof(AdoptCatalogueSpecialTypes), () => _world.AdoptCatalogueSpecialTypes(specialTypes));

    /// <inheritdoc/>
    public void AdoptCatalogueBaseTypes(IReadOnlyDictionary<string, string> baseTypes) =>
        Guard(nameof(AdoptCatalogueBaseTypes), () => _world.AdoptCatalogueBaseTypes(baseTypes));

    /// <inheritdoc/>
    public EpisodeSettings ReadSettings() => Guard(nameof(ReadSettings), _world.ReadSettings);

    /// <inheritdoc/>
    public void WriteSettings(EpisodeSettings settings) =>
        Guard(nameof(WriteSettings), () => _world.WriteSettings(settings));

    /// <inheritdoc/>
    public ActorId Spawn(string blueprintId, Transform at, string roleName) =>
        Guard(nameof(Spawn), () => _world.Spawn(blueprintId, at, roleName));

    /// <inheritdoc/>
    public IReadOnlyList<CommandResponse> ApplyBatch(IReadOnlyList<Command> commands) =>
        Guard(nameof(ApplyBatch), () => _world.ApplyBatch(commands));

    /// <inheritdoc/>
    public RenderSetWrite WriteRenderSet(IReadOnlyList<LentBody> lent, IReadOnlyList<ActorId> parked) =>
        Guard(nameof(WriteRenderSet), () => _world.WriteRenderSet(lent, parked));

    /// <inheritdoc/>
    public DrawDistanceWrite WriteDrawDistance(IReadOnlyList<ActorId> bodies, double metres) =>
        Guard(nameof(WriteDrawDistance), () => _world.WriteDrawDistance(bodies, metres));

    /// <inheritdoc/>
    public Transform? ObservedTransform(ActorId actor) =>
        Guard(nameof(ObservedTransform), () => _world.ObservedTransform(actor));

    /// <inheritdoc/>
    public Vector3D? ObservedVelocity(ActorId actor) =>
        Guard(nameof(ObservedVelocity), () => _world.ObservedVelocity(actor));

    /// <inheritdoc/>
    public Transform? ObservedTransformAt(ActorId actor, ulong frame) =>
        Guard(nameof(ObservedTransformAt), () => _world.ObservedTransformAt(actor, frame));

    /// <inheritdoc/>
    public CameraOptics? DescribeCamera(ActorId camera) =>
        Guard(nameof(DescribeCamera), () => _world.DescribeCamera(camera));

    /// <inheritdoc/>
    public ulong? Tick() => Guard(nameof(Tick), _world.Tick);

    /// <inheritdoc/>
    public void WriteLayerVisible(string layer, bool visible) =>
        Guard(nameof(WriteLayerVisible), () => _world.WriteLayerVisible(layer, visible));

    /// <inheritdoc/>
    public IReadOnlyList<double> ReadSolarState() => Guard(nameof(ReadSolarState), _world.ReadSolarState);

    /// <inheritdoc/>
    public bool WriteSolarEpoch(int year, int month, int day, double hours, double utcOffsetHours) =>
        Guard(nameof(WriteSolarEpoch), () => _world.WriteSolarEpoch(year, month, day, hours, utcOffsetHours));

    /// <inheritdoc/>
    public bool WriteTimeAdvance(bool advancing, double rate) =>
        Guard(nameof(WriteTimeAdvance), () => _world.WriteTimeAdvance(advancing, rate));

    /// <inheritdoc/>
    public IReadOnlyList<double> ObservedSolarState() =>
        Guard(nameof(ObservedSolarState), _world.ObservedSolarState);

    /// <summary>Whether a failure is the connection to the world failing.</summary>
    public static bool IsConnectionFailure(Exception failure) =>
        failure is IOException or SocketException or TimeoutException;

    private static void Guard(string operation, Action call) =>
        Guard(operation, () =>
        {
            call();
            return true;
        });

    private static T Guard<T>(string operation, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception failure) when (IsConnectionFailure(failure))
        {
            throw new WorldConnectionLostException(operation, failure);
        }
    }
}
