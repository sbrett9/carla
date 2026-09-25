// §10.14 — FWorldObserver / EpisodeState.
// After 48-byte header: the EpisodeState header + N * 119-byte ActorDynamicState.
// The EpisodeState header is the original 36 bytes plus the solar block at offset 36: 11 doubles,
// or 12 where the server also carries the refraction-corrected elevation (EpisodeStateLayout).
// static_assert(sizeof(ActorDynamicState) == 119) — verified in source (§13.6).
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Enums;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Sensors;

[Flags]
public enum SimulationState : byte
{
    None = 0x0,
    MapChange = 0x1,
    PendingLightUpdate = 0x2,
    /// The header's solar block carries a sun that was actually measured this tick. Its defaults
    /// are a well-formed reading -- midnight of year 0 at latitude 0, longitude 0 -- so nothing in
    /// the values themselves says "this world has no sun"; only this flag does.
    SolarStateValid = 0x4,
    /// The header's solar block is twelve doubles wide: the refraction-corrected elevation follows
    /// the rate. Set on every snapshot from a server that carries it, sun or no sun, because it
    /// describes where the actors start rather than what was measured.
    SolarCorrectedElevationCarried = 0x8
}

public sealed class EpisodeStateHeader
{
    public ulong EpisodeId { get; init; }
    public double PlatformTimestamp { get; init; }
    public float DeltaSeconds { get; init; }
    public Vector3DInt MapOrigin { get; init; }
    public SimulationState SimulationState { get; init; }

    /// Solar / time-of-day state in effect this tick (appended to the header, offset 36):
    /// [solar_time, year, month, day, time_zone, lat, lon, elevation_deg, azimuth_deg, advancing, rate,
    /// corrected_elevation_deg], the last only where the server carries it.
    /// Empty when the world has no CesiumSunSky (SimulationState.SolarStateValid clear), so a
    /// consumer never mistakes the header's defaults for a measured sun.
    public IReadOnlyList<double> Solar { get; init; } = System.Array.Empty<double>();
}

public sealed class ActorDynamicState
{
    public uint Id { get; init; }
    public ActorState State { get; init; }
    public Transform Transform { get; init; }
    public Vector3D Velocity { get; init; }
    public Vector3D AngularVelocity { get; init; }
    public Vector3D Acceleration { get; init; }
    public ReadOnlyMemory<byte> TypeDependentState { get; init; } // 54 bytes, caller parses
}

public sealed class EpisodeStateSensorData
{
    public EpisodeStateHeader Header { get; }
    public IReadOnlyList<ActorDynamicState> Actors { get; }

    private EpisodeStateSensorData(EpisodeStateHeader h, IReadOnlyList<ActorDynamicState> a)
    { Header = h; Actors = a; }

    public static EpisodeStateSensorData Deserialize(ReadOnlySpan<byte> payload)
    {
        ulong episodeId      = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        double platformTs    = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload[8..]));
        float deltaSeconds   = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[16..]));
        int mx = BinaryPrimitives.ReadInt32LittleEndian(payload[20..]);
        int my = BinaryPrimitives.ReadInt32LittleEndian(payload[24..]);
        int mz = BinaryPrimitives.ReadInt32LittleEndian(payload[28..]);
        var simState         = (SimulationState)payload[EpisodeStateLayout.FlagsOffset];
        // 3 bytes padding at [33..35], then the solar block at offset 36. It is read only when the
        // header says a sun was measured; otherwise it holds defaults that read as a real sun.
        var solar = EpisodeStateLayout.ReadSolar(payload);

        var header = new EpisodeStateHeader
        {
            EpisodeId = episodeId, PlatformTimestamp = platformTs,
            DeltaSeconds = deltaSeconds, MapOrigin = new Vector3DInt(mx, my, mz),
            SimulationState = simState, Solar = solar
        };

        int stateHeaderSize = EpisodeStateLayout.HeaderSize(payload);
        var actorData = payload[stateHeaderSize..];
        const int ActorSize = 119;
        int count = actorData.Length / ActorSize;
        var actors = new ActorDynamicState[count];

        for (int i = 0; i < count; i++)
        {
            var a = actorData.Slice(i * ActorSize, ActorSize);
            uint id         = BinaryPrimitives.ReadUInt32LittleEndian(a);
            var state       = (ActorState)a[4];
            // Transform at [5]: Location(12 bytes) + Rotation(12 bytes)
            float lx = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[5..]));
            float ly = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[9..]));
            float lz = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[13..]));
            float rp = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[17..]));
            float ry = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[21..]));
            float rr = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[25..]));
            // Velocity at [29], AngularVelocity at [41], Acceleration at [53]
            float vx = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[29..]));
            float vy = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[33..]));
            float vz = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[37..]));
            float avx = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[41..]));
            float avy = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[45..]));
            float avz = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[49..]));
            float ax = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[53..]));
            float ay = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[57..]));
            float az = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(a[61..]));

            actors[i] = new ActorDynamicState
            {
                Id = id, State = state,
                Transform = new Transform(new Location(lx, ly, lz), new Rotation(rp, ry, rr)),
                Velocity = new Vector3D(vx, vy, vz),
                AngularVelocity = new Vector3D(avx, avy, avz),
                Acceleration = new Vector3D(ax, ay, az),
                TypeDependentState = a[65..].ToArray() // 54 bytes
            };
        }
        return new EpisodeStateSensorData(header, actors);
    }
}
