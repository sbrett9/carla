// Source: carla/rpc/Texture.h — TextureColor = Texture<sensor::data::Color>
// MSGPACK_DEFINE_ARRAY(_width, _height, _texture_data)
namespace CarlaNet.Types.Rpc.Texture;

[MessagePackObject]
public record struct TextureColor(
    [property: Key(0)] uint Width,
    [property: Key(1)] uint Height,
    [property: Key(2)] IReadOnlyList<SensorPixelColor> Data);
