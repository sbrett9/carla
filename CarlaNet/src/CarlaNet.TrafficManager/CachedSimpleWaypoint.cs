// Source: carla/trafficmanager/CachedSimpleWaypoint.h + CachedSimpleWaypoint.cpp
//
// Serializable mirror of <see cref="SimpleWaypoint"/> used by InMemoryMap's
// cooked-cache (.bin) writer/reader. Upstream writes one of these per
// SimpleWaypoint to a single contiguous binary blob; on subsequent loads
// the in-memory graph is rebuilt by id lookup, avoiding the cost of
// re-running Map.GenerateTopology + GenerateWaypoints.
//
// On-disk layout (little-endian, native widths) — must match upstream
// CachedSimpleWaypoint.cpp byte-for-byte so .bin caches produced by the C++
// build are forward-readable. Layout:
//
//   uint64  waypoint_id
//   uint32  road_id
//   uint32  section_id
//   int32   lane_id
//   float   s
//   uint16  next_waypoints.size()
//   uint64[next_waypoints.size()]   next ids
//   uint16  previous_waypoints.size()
//   uint64[previous_waypoints.size()] previous ids
//   uint64  next_left_waypoint  (0 = none)
//   uint64  next_right_waypoint (0 = none)
//   int32   geodesic_grid_id
//   bool    is_junction         (1 byte)
//   uint8   road_option
//
// Wave 3's InMemoryMap.cpp port will call Write/Read on each record.
#nullable enable

namespace CarlaNet.TrafficManager;

/// <summary>
/// Serializable POD form of <see cref="SimpleWaypoint"/>. Each record holds
/// the wrapped Waypoint identity + ids of its graph neighbours (so the graph
/// can be re-linked on load by id lookup).
/// </summary>
internal sealed class CachedSimpleWaypoint
{
    public ulong WaypointId;
    public uint RoadId;
    public uint SectionId;
    public int LaneId;
    public float S;

    public List<ulong> NextWaypoints { get; } = new();
    public List<ulong> PreviousWaypoints { get; } = new();

    public ulong NextLeftWaypoint;
    public ulong NextRightWaypoint;

    public int GeodesicGridId;
    public bool IsJunction;
    public byte RoadOption;

    public CachedSimpleWaypoint() { }

    /// <summary>
    /// Build a cached record from a live SimpleWaypoint. Mirrors the
    /// upstream constructor in CachedSimpleWaypoint.cpp:14–39.
    /// </summary>
    public CachedSimpleWaypoint(SimpleWaypoint simpleWaypoint)
    {
        WaypointId = simpleWaypoint.GetId();

        var wp = simpleWaypoint.GetWaypoint();
        RoadId = wp.RoadId;
        SectionId = wp.SectionId;
        LaneId = wp.LaneId;
        S = (float)wp.S;

        foreach (var nxt in simpleWaypoint.GetNextWaypoint())
            NextWaypoints.Add(nxt.GetId());
        foreach (var prv in simpleWaypoint.GetPreviousWaypoint())
            PreviousWaypoints.Add(prv.GetId());

        if (simpleWaypoint.GetLeftWaypoint() is { } left)
            NextLeftWaypoint = left.GetId();
        if (simpleWaypoint.GetRightWaypoint() is { } right)
            NextRightWaypoint = right.GetId();

        GeodesicGridId = simpleWaypoint.GetGeodesicGridId();
        IsJunction = simpleWaypoint.CheckJunction();
        RoadOption = (byte)simpleWaypoint.GetRoadOption();
    }

    // ── Binary write (matches upstream byte layout) ────────────────────────

    /// <summary>
    /// Serialize this record to a <see cref="System.IO.BinaryWriter"/>. The
    /// writer must wrap a stream opened with <see cref="System.IO.FileMode.Create"/>
    /// (or append) in the upstream cache file. BinaryWriter writes little-
    /// endian by default — matches the upstream <c>std::ofstream</c> on x86/x64.
    /// </summary>
    public void Write(System.IO.BinaryWriter writer)
    {
        writer.Write(WaypointId);

        writer.Write(RoadId);
        writer.Write(SectionId);
        writer.Write(LaneId);
        writer.Write(S);

        // next list
        ushort totalNext = (ushort)NextWaypoints.Count;
        writer.Write(totalNext);
        for (int i = 0; i < totalNext; i++)
            writer.Write(NextWaypoints[i]);

        // previous list
        ushort totalPrev = (ushort)PreviousWaypoints.Count;
        writer.Write(totalPrev);
        for (int i = 0; i < totalPrev; i++)
            writer.Write(PreviousWaypoints[i]);

        writer.Write(NextLeftWaypoint);
        writer.Write(NextRightWaypoint);

        writer.Write(GeodesicGridId);
        writer.Write(IsJunction);
        writer.Write(RoadOption);
    }

    // ── Binary read variants (match upstream's two overloads) ──────────────

    /// <summary>
    /// Deserialize from a <see cref="System.IO.BinaryReader"/>. The reader
    /// position advances past the record. Matches upstream's
    /// <c>Read(std::ifstream&amp;)</c>.
    /// </summary>
    public void Read(System.IO.BinaryReader reader)
    {
        WaypointId = reader.ReadUInt64();

        RoadId = reader.ReadUInt32();
        SectionId = reader.ReadUInt32();
        LaneId = reader.ReadInt32();
        S = reader.ReadSingle();

        ushort totalNext = reader.ReadUInt16();
        for (int i = 0; i < totalNext; i++)
            NextWaypoints.Add(reader.ReadUInt64());

        ushort totalPrev = reader.ReadUInt16();
        for (int i = 0; i < totalPrev; i++)
            PreviousWaypoints.Add(reader.ReadUInt64());

        NextLeftWaypoint = reader.ReadUInt64();
        NextRightWaypoint = reader.ReadUInt64();

        GeodesicGridId = reader.ReadInt32();
        IsJunction = reader.ReadBoolean();
        RoadOption = reader.ReadByte();
    }

    /// <summary>
    /// Deserialize from a byte buffer at the supplied offset, advancing the
    /// offset past the record. Mirrors upstream's
    /// <c>Read(const std::vector&lt;uint8_t&gt;&amp;, unsigned long&amp;)</c>
    /// (used when the cache is bulk-loaded into memory).
    /// </summary>
    public void Read(ReadOnlySpan<byte> content, ref int start)
    {
        WaypointId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(content[start..]); start += 8;

        RoadId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(content[start..]); start += 4;
        SectionId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(content[start..]); start += 4;
        LaneId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(content[start..]); start += 4;
        S = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(content[start..]); start += 4;

        ushort totalNext = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(content[start..]); start += 2;
        for (int i = 0; i < totalNext; i++)
        {
            NextWaypoints.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(content[start..]));
            start += 8;
        }

        ushort totalPrev = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(content[start..]); start += 2;
        for (int i = 0; i < totalPrev; i++)
        {
            PreviousWaypoints.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(content[start..]));
            start += 8;
        }

        NextLeftWaypoint = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(content[start..]); start += 8;
        NextRightWaypoint = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(content[start..]); start += 8;

        GeodesicGridId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(content[start..]); start += 4;
        IsJunction = content[start] != 0; start += 1;
        RoadOption = content[start]; start += 1;
    }
}
