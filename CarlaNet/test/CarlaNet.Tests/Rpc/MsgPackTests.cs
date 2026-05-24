// Mirrors LibCarla/source/test/common/test_msgpack.cpp
// Tests msgpack serialization for all RPC types: round-trip fidelity, optional<T>, key ordering.
using System.Buffers;
using CarlaNet.Types.Rpc;
using CarlaNet.Types.Rpc.Actors;
using CarlaNet.Types.Rpc.Control;
using CarlaNet.Types.Rpc.Environment;
using CarlaNet.Types.Geom;
using MessagePack;

namespace CarlaNet.Tests.Rpc;

public class MsgPackTests
{
    // ── Actor ────────────────────────────────────────────────────────────────

    [Fact]
    public void Actor_MsgPack_RoundTrip()
    {
        var actor = new Actor(
            Id: 42u,
            ParentId: 0u,
            Description: new ActorDescription(2u, "actor.random.whatever", []),
            BoundingBox: new BoundingBox(
                new Location(0f, 0f, 0f),
                new Vector3D(1f, 2f, 3f),
                new Rotation(0f, 0f, 0f)),
            SemanticTags: [],
            StreamToken: []);

        var bytes = MessagePackSerializer.Serialize(actor);
        var result = MessagePackSerializer.Deserialize<Actor>(bytes);

        Assert.Equal(actor.Id, result.Id);
        Assert.Equal(actor.ParentId, result.ParentId);
        Assert.Equal(actor.Description.Uid, result.Description.Uid);
        Assert.Equal(actor.Description.Id, result.Description.Id);
        Assert.Equal(actor.BoundingBox.Extent.X, result.BoundingBox.Extent.X);
        Assert.Equal(actor.BoundingBox.Extent.Y, result.BoundingBox.Extent.Y);
        Assert.Equal(actor.BoundingBox.Extent.Z, result.BoundingBox.Extent.Z);
    }

    [Fact]
    public void Actor_MsgPack_WithSemanticTags()
    {
        var actor = new Actor(
            Id: 7u,
            ParentId: 1u,
            Description: new ActorDescription(5u, "vehicle.tesla.model3", []),
            BoundingBox: new BoundingBox(default, new Vector3D(2f, 1f, 0.8f), default),
            SemanticTags: [10, 20],
            StreamToken: []);

        var bytes = MessagePackSerializer.Serialize(actor);
        var result = MessagePackSerializer.Deserialize<Actor>(bytes);

        Assert.Equal(2, result.SemanticTags.Length);
        Assert.Equal(10, result.SemanticTags[0]);
        Assert.Equal(20, result.SemanticTags[1]);
    }

    // ── EpisodeSettings — std::optional<double> ──────────────────────────────

    [Fact]
    public void EpisodeSettings_Null_Optional_RoundTrip()
    {
        var settings = new EpisodeSettings(
            SynchronousMode: false, NoRenderingMode: false, FixedDeltaSeconds: null,
            Substepping: true, MaxSubstepDeltaTime: 0.01, MaxSubsteps: 10,
            MaxCullingDistance: 0f, DeterministicRagdolls: true,
            TileStreamDistance: 3000f, ActorActiveDistance: 2000f, SpectatorAsEgo: true);

        var bytes = MessagePackSerializer.Serialize(settings);
        var result = MessagePackSerializer.Deserialize<EpisodeSettings>(bytes);

        Assert.Null(result.FixedDeltaSeconds);
        Assert.False(result.SynchronousMode);
        Assert.True(result.Substepping);
    }

    [Fact]
    public void EpisodeSettings_Value_Optional_RoundTrip()
    {
        var settings = new EpisodeSettings(
            SynchronousMode: true, NoRenderingMode: false, FixedDeltaSeconds: 0.05,
            Substepping: true, MaxSubstepDeltaTime: 0.01, MaxSubsteps: 10,
            MaxCullingDistance: 0f, DeterministicRagdolls: true,
            TileStreamDistance: 3000f, ActorActiveDistance: 2000f, SpectatorAsEgo: false);

        var bytes = MessagePackSerializer.Serialize(settings);
        var result = MessagePackSerializer.Deserialize<EpisodeSettings>(bytes);

        Assert.NotNull(result.FixedDeltaSeconds);
        Assert.Equal(0.05, result.FixedDeltaSeconds!.Value, 10);
        Assert.True(result.SynchronousMode);
    }

    // ── VehicleControl ────────────────────────────────────────────────────────

    [Fact]
    public void VehicleControl_MsgPack_RoundTrip()
    {
        var ctrl = new VehicleControl(0.5f, 0.1f, 0f, false, false, false, 3);
        var bytes = MessagePackSerializer.Serialize(ctrl);
        var result = MessagePackSerializer.Deserialize<VehicleControl>(bytes);
        Assert.Equal(ctrl.Throttle, result.Throttle);
        Assert.Equal(ctrl.Steer,   result.Steer);
        Assert.Equal(ctrl.Brake,   result.Brake);
        Assert.Equal(ctrl.Gear,    result.Gear);
    }

    [Fact]
    public void VehicleControl_MsgPack_Reverse_Flags()
    {
        var ctrl = new VehicleControl(0f, 0f, 1.0f, true, true, true, -1);
        var bytes = MessagePackSerializer.Serialize(ctrl);
        var result = MessagePackSerializer.Deserialize<VehicleControl>(bytes);
        Assert.True(result.HandBrake);
        Assert.True(result.Reverse);
        Assert.True(result.ManualGearShift);
        Assert.Equal(-1, result.Gear);
    }

    // ── ActorAttribute ────────────────────────────────────────────────────────

    [Fact]
    public void ActorAttribute_MsgPack_RoundTrip()
    {
        var attr = new ActorAttribute(
            "color", ActorAttributeType.RGBColor, "255,0,0",
            ["255,255,255", "0,0,0"], true, false);

        var bytes = MessagePackSerializer.Serialize(attr);
        var result = MessagePackSerializer.Deserialize<ActorAttribute>(bytes);

        Assert.Equal(attr.Id, result.Id);
        Assert.Equal(attr.Type, result.Type);
        Assert.Equal(attr.Value, result.Value);
        Assert.Equal(2, result.RecommendedValues.Count);
        Assert.Equal("255,255,255", result.RecommendedValues[0]);
        Assert.True(result.IsModifiable);
        Assert.False(result.RestrictToRecommended);
    }

    [Fact]
    public void ActorAttribute_MsgPack_Int_Type()
    {
        var attr = new ActorAttribute("number_of_wheels", ActorAttributeType.Int, "4", [], false, false);
        var bytes = MessagePackSerializer.Serialize(attr);
        var result = MessagePackSerializer.Deserialize<ActorAttribute>(bytes);
        Assert.Equal(ActorAttributeType.Int, result.Type);
        Assert.Equal("4", result.Value);
        Assert.Empty(result.RecommendedValues);
    }

    // ── WeatherParameters ────────────────────────────────────────────────────

    [Fact]
    public void WeatherParameters_MsgPack_RoundTrip()
    {
        var weather = new WeatherParameters(
            50f, 20f, 0f, 10f, 180f, 45f, 5f, 100f,
            0.1f, 0.3f, 0f, 0.03f, 0.0331f, 0f);

        var bytes = MessagePackSerializer.Serialize(weather);
        var result = MessagePackSerializer.Deserialize<WeatherParameters>(bytes);

        Assert.Equal(weather.Cloudiness,              result.Cloudiness);
        Assert.Equal(weather.Precipitation,           result.Precipitation);
        Assert.Equal(weather.RayleighScatteringScale, result.RayleighScatteringScale);
        Assert.Equal(weather.SunAzimuthAngle,         result.SunAzimuthAngle);
        Assert.Equal(weather.SunAltitudeAngle,        result.SunAltitudeAngle);
    }

    [Fact]
    public void WeatherParameters_MsgPack_AllZero()
    {
        var weather = new WeatherParameters(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
        var bytes = MessagePackSerializer.Serialize(weather);
        var result = MessagePackSerializer.Deserialize<WeatherParameters>(bytes);
        Assert.Equal(0f, result.Cloudiness);
        Assert.Equal(0f, result.DustStorm);
    }

    // ── Color — wire order R, G, B (Key 0, 1, 2) ────────────────────────────

    [Fact]
    public void Color_MsgPack_RoundTrip()
    {
        var color = new Color(255, 128, 0);
        var bytes = MessagePackSerializer.Serialize(color);
        var result = MessagePackSerializer.Deserialize<Color>(bytes);
        Assert.Equal(color.R, result.R);
        Assert.Equal(color.G, result.G);
        Assert.Equal(color.B, result.B);
    }

    [Fact]
    public void Color_MsgPack_KeyOrder_RGB()
    {
        // Source: carla/rpc/Color.h — MSGPACK_DEFINE_ARRAY(r, g, b)
        // Wire: [R, G, B] — 3-element fixarray
        var color = new Color(255, 128, 0);
        var bytes = MessagePackSerializer.Serialize(color);
        var reader = new MessagePackReader(new ReadOnlySequence<byte>(bytes));
        Assert.Equal(3, reader.ReadArrayHeader());
        Assert.Equal(255, reader.ReadByte());  // R at Key(0)
        Assert.Equal(128, reader.ReadByte());  // G at Key(1)
        Assert.Equal(0,   reader.ReadByte());  // B at Key(2)
    }

    // ── WalkerBoneControlIn ────────────────────────────────────────────────

    [Fact]
    public void WalkerBoneControlIn_MsgPack_RoundTrip()
    {
        var bones = new WalkerBoneControlIn([
            new BoneTransformDataIn("spine_01",
                new Transform(new Location(0f, 0f, 0f), new Rotation(0f, 0f, 0f))),
            new BoneTransformDataIn("head",
                new Transform(new Location(0f, 0f, 1f), new Rotation(10f, 0f, 0f)))
        ]);

        var bytes = MessagePackSerializer.Serialize(bones);
        var result = MessagePackSerializer.Deserialize<WalkerBoneControlIn>(bytes);

        Assert.Equal(2, result.BoneTransforms.Count);
        Assert.Equal("spine_01", result.BoneTransforms[0].BoneName);
        Assert.Equal("head",     result.BoneTransforms[1].BoneName);
        Assert.Equal(1f,  result.BoneTransforms[1].Transform.Location.Z);
        Assert.Equal(10f, result.BoneTransforms[1].Transform.Rotation.Pitch);
    }

    [Fact]
    public void WalkerBoneControlIn_MsgPack_EmptyList()
    {
        var bones = new WalkerBoneControlIn([]);
        var bytes = MessagePackSerializer.Serialize(bones);
        var result = MessagePackSerializer.Deserialize<WalkerBoneControlIn>(bytes);
        Assert.Empty(result.BoneTransforms);
    }

    // ── ActorDescription ──────────────────────────────────────────────────────

    [Fact]
    public void ActorDescription_MsgPack_RoundTrip()
    {
        var desc = new ActorDescription(42u, "sensor.camera.rgb",
            [new ActorAttributeValue("image_size_x", ActorAttributeType.Int, "800")]);

        var bytes = MessagePackSerializer.Serialize(desc);
        var result = MessagePackSerializer.Deserialize<ActorDescription>(bytes);

        Assert.Equal(42u, result.Uid);
        Assert.Equal("sensor.camera.rgb", result.Id);
        Assert.Single(result.Attributes);
        Assert.Equal("image_size_x", result.Attributes[0].Id);
    }
}
