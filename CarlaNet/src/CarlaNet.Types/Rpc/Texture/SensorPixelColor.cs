// Source: carla/rpc/Texture.h
// sensor::data::Color is {B,G,R,A} byte layout — for texture upload purposes only.
namespace CarlaNet.Types.Rpc.Texture;

[MessagePackObject]
public record struct SensorPixelColor(
    [property: Key(0)] byte B,
    [property: Key(1)] byte G,
    [property: Key(2)] byte R,
    [property: Key(3)] byte A);
