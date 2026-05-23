// Source: carla/rpc/Texture.h — MSGPACK_DEFINE_ARRAY(_width, _height, _texture_data)
// TextureColor = Texture<sensor::data::Color>, TextureFloatColor = Texture<FloatColor>
// sensor::data::Color is {B,G,R,A} byte layout — for texture upload purposes only.
namespace CarlaNet.Types.Rpc.Texture;

[MessagePackObject]
public record struct SensorPixelColor(
    [property: Key(0)] byte B,
    [property: Key(1)] byte G,
    [property: Key(2)] byte R,
    [property: Key(3)] byte A);

[MessagePackObject]
public record struct TextureColor(
    [property: Key(0)] uint Width,
    [property: Key(1)] uint Height,
    [property: Key(2)] IReadOnlyList<SensorPixelColor> Data);

[MessagePackObject]
public record struct TextureFloatColor(
    [property: Key(0)] uint Width,
    [property: Key(1)] uint Height,
    [property: Key(2)] IReadOnlyList<FloatColor> Data);
