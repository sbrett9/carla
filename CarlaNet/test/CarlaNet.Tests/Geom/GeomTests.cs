// Mirrors LibCarla/source/test/common/test_geom.cpp
// CarlaNet geometry types are pure wire-protocol data records; tests focus on
// MessagePack round-trip fidelity and the binary key ordering mandated by MSGPACK_DEFINE_ARRAY.
using System.Buffers;
using CarlaNet.Types.Geom;
using MessagePack;

namespace CarlaNet.Tests.Geom;

public class GeomTests
{
    // ── Vector3D ────────────────────────────────────────────────────────────

    [Fact]
    public void Vector3D_MsgPack_RoundTrip()
    {
        var v = new Vector3D(1.0f, 2.0f, 3.0f);
        var bytes = MessagePackSerializer.Serialize(v);
        var v2 = MessagePackSerializer.Deserialize<Vector3D>(bytes);
        Assert.Equal(v.X, v2.X);
        Assert.Equal(v.Y, v2.Y);
        Assert.Equal(v.Z, v2.Z);
    }

    [Fact]
    public void Vector3D_MsgPack_Negative_Values()
    {
        var v = new Vector3D(-100.5f, 0f, 999.9f);
        var bytes = MessagePackSerializer.Serialize(v);
        var v2 = MessagePackSerializer.Deserialize<Vector3D>(bytes);
        Assert.Equal(v.X, v2.X);
        Assert.Equal(v.Z, v2.Z);
    }

    [Fact]
    public void Vector3D_MsgPack_KeyOrdering_XYZ()
    {
        // MSGPACK_DEFINE_ARRAY(x,y,z) — 3-element array, x first
        var v = new Vector3D(7.0f, 0f, 0f);
        var bytes = MessagePackSerializer.Serialize(v);
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
        Assert.Equal(3, reader.ReadArrayHeader());
        Assert.Equal(7.0f, reader.ReadSingle());  // x at Key(0)
    }

    // ── Vector2D ────────────────────────────────────────────────────────────

    [Fact]
    public void Vector2D_MsgPack_RoundTrip()
    {
        var v = new Vector2D(3.14f, 2.71f);
        var bytes = MessagePackSerializer.Serialize(v);
        var v2 = MessagePackSerializer.Deserialize<Vector2D>(bytes);
        Assert.Equal(v.X, v2.X);
        Assert.Equal(v.Y, v2.Y);
    }

    // ── Location ─────────────────────────────────────────────────────────────

    [Fact]
    public void Location_MsgPack_RoundTrip()
    {
        var loc = new Location(-3.14f, 1.337f, 4.20f);
        var bytes = MessagePackSerializer.Serialize(loc);
        var loc2 = MessagePackSerializer.Deserialize<Location>(bytes);
        Assert.Equal(loc.X, loc2.X);
        Assert.Equal(loc.Y, loc2.Y);
        Assert.Equal(loc.Z, loc2.Z);
    }

    [Fact]
    public void Location_Default_Is_Zero()
    {
        var loc = default(Location);
        Assert.Equal(0f, loc.X);
        Assert.Equal(0f, loc.Y);
        Assert.Equal(0f, loc.Z);
    }

    // ── Rotation ─────────────────────────────────────────────────────────────

    [Fact]
    public void Rotation_MsgPack_RoundTrip()
    {
        var rot = new Rotation(-47.0f, 37.0f, 250.2f);
        var bytes = MessagePackSerializer.Serialize(rot);
        var rot2 = MessagePackSerializer.Deserialize<Rotation>(bytes);
        Assert.Equal(rot.Pitch, rot2.Pitch);
        Assert.Equal(rot.Yaw,   rot2.Yaw);
        Assert.Equal(rot.Roll,  rot2.Roll);
    }

    [Fact]
    public void Rotation_MsgPack_KeyOrdering_PitchYawRoll()
    {
        // MSGPACK_DEFINE_ARRAY(pitch, yaw, roll)
        var rot = new Rotation(10f, 20f, 30f);
        var bytes = MessagePackSerializer.Serialize(rot);
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
        Assert.Equal(3, reader.ReadArrayHeader());
        Assert.Equal(10f, reader.ReadSingle());  // pitch at Key(0)
        Assert.Equal(20f, reader.ReadSingle());  // yaw   at Key(1)
        Assert.Equal(30f, reader.ReadSingle());  // roll  at Key(2)
    }

    // ── Transform ────────────────────────────────────────────────────────────

    [Fact]
    public void Transform_MsgPack_RoundTrip()
    {
        var t = new Transform(new Location(1.0f, 2.0f, 3.0f), new Rotation(0f, 90f, 0f));
        var bytes = MessagePackSerializer.Serialize(t);
        var t2 = MessagePackSerializer.Deserialize<Transform>(bytes);
        Assert.Equal(t.Location.X, t2.Location.X);
        Assert.Equal(t.Location.Y, t2.Location.Y);
        Assert.Equal(t.Location.Z, t2.Location.Z);
        Assert.Equal(t.Rotation.Yaw, t2.Rotation.Yaw);
    }

    [Fact]
    public void Transform_MsgPack_KeyOrdering_LocationThenRotation()
    {
        // Key(0)=Location (3-array), Key(1)=Rotation (3-array)
        var t = new Transform(new Location(1f, 0f, 0f), new Rotation(0f, 0f, 0f));
        var bytes = MessagePackSerializer.Serialize(t);
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
        Assert.Equal(2, reader.ReadArrayHeader());   // outer [location, rotation]
        Assert.Equal(3, reader.ReadArrayHeader());   // location is [x, y, z]
        Assert.Equal(1f, reader.ReadSingle());       // x = 1.0
    }

    [Fact]
    public void Transform_Default_Fields_Are_Zero()
    {
        var t = new Transform(default, default);
        Assert.Equal(0f, t.Location.X);
        Assert.Equal(0f, t.Rotation.Yaw);
    }

    // ── BoundingBox ──────────────────────────────────────────────────────────

    [Fact]
    public void BoundingBox_MsgPack_RoundTrip()
    {
        var bb = new BoundingBox(
            new Location(10.2f, -32.4f, 15.6f),
            new Vector3D(9.2f, 13.5f, 20.3f),
            new Rotation(0f, 0f, 0f));
        var bytes = MessagePackSerializer.Serialize(bb);
        var bb2 = MessagePackSerializer.Deserialize<BoundingBox>(bytes);
        Assert.Equal(bb.Location.X,  bb2.Location.X);
        Assert.Equal(bb.Extent.X,    bb2.Extent.X);
        Assert.Equal(bb.Extent.Y,    bb2.Extent.Y);
        Assert.Equal(bb.Extent.Z,    bb2.Extent.Z);
        Assert.Equal(bb.Rotation.Yaw, bb2.Rotation.Yaw);
    }

    [Fact]
    public void BoundingBox_MsgPack_KeyOrdering_LocationExtentRotation()
    {
        // MSGPACK_DEFINE_ARRAY(location, extent, rotation) — 3-element outer array
        var bb = new BoundingBox(
            new Location(5f, 0f, 0f),
            new Vector3D(1f, 1f, 1f),
            new Rotation(0f, 0f, 0f));
        var bytes = MessagePackSerializer.Serialize(bb);
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
        Assert.Equal(3, reader.ReadArrayHeader());  // outer [location, extent, rotation]
        Assert.Equal(3, reader.ReadArrayHeader());  // location = [x, y, z]
        Assert.Equal(5f, reader.ReadSingle());      // location.x
    }

    // ── GeoLocation ──────────────────────────────────────────────────────────

    [Fact]
    public void GeoLocation_MsgPack_RoundTrip()
    {
        var geo = new GeoLocation(48.8566, 2.3522, 35.0);
        var bytes = MessagePackSerializer.Serialize(geo);
        var geo2 = MessagePackSerializer.Deserialize<GeoLocation>(bytes);
        Assert.Equal(geo.Latitude,  geo2.Latitude,  10);
        Assert.Equal(geo.Longitude, geo2.Longitude, 10);
        Assert.Equal(geo.Altitude,  geo2.Altitude,  10);
    }

    [Fact]
    public void GeoLocation_MsgPack_KeyOrdering_LatLonAlt()
    {
        var geo = new GeoLocation(1.0, 2.0, 3.0);
        var bytes = MessagePackSerializer.Serialize(geo);
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
        Assert.Equal(3, reader.ReadArrayHeader());
        Assert.Equal(1.0, reader.ReadDouble());  // latitude at Key(0)
        Assert.Equal(2.0, reader.ReadDouble());  // longitude at Key(1)
        Assert.Equal(3.0, reader.ReadDouble());  // altitude at Key(2)
    }
}
