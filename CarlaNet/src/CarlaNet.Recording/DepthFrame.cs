using CarlaNet.Sensors;
using CarlaNet.Transport.Streaming;
using CarlaNet.Types.Geom;

namespace CarlaNet.Recording;

/// <summary>
/// One capture from a <c>sensor.camera.depth</c>, kept in the form a geometric test needs: the raw
/// pixel buffer plus the pose, size and field of view it was rendered with, and the simulation frame
/// it belongs to. CARLA packs range into the colour channels; <see cref="RangeAt"/> unpacks them.
/// </summary>
public sealed class DepthFrame
{
    /// <summary>
    /// The range the 24-bit colour value is spread linearly over — the depth material's far plane.
    /// A decoded range therefore never exceeds this, and sky reads as (almost exactly) this.
    /// </summary>
    public const double FarPlaneMetres = 1000.0;

    private const double ColourScale = 256.0 * 256.0 * 256.0 - 1.0;

    private readonly ReadOnlyMemory<byte> _bgra;

    public ulong Frame { get; }
    public double Timestamp { get; }

    /// <summary>The pose the depth pixels were rendered from — taken from this frame's own sensor
    /// header, so the geometry always matches the pixels rather than a pose read separately.</summary>
    public Transform Transform { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Horizontal field of view in degrees, as the frame itself reports it.</summary>
    public double HFovDeg { get; }

    /// <summary>Assemble a capture from its decoded parts — a frame off the wire, or one replayed or
    /// synthesised from a recorded depth product.</summary>
    public DepthFrame(ulong frame, double timestamp, Transform transform,
                      int width, int height, double hFovDeg, ReadOnlyMemory<byte> bgra)
    {
        Frame = frame;
        Timestamp = timestamp;
        Transform = transform;
        Width = width;
        Height = height;
        HFovDeg = hFovDeg;
        _bgra = bgra;
    }

    /// <summary>Decode a streamed depth capture, or null if the payload is not a usable image.</summary>
    public static DepthFrame? FromSensorFrame(SensorFrame frame)
    {
        ImageSensorData image;
        try { image = ImageSensorData.Deserialize(frame.Payload.Span); }
        catch { return null; }

        int w = (int)image.Width, h = (int)image.Height;
        if (w <= 0 || h <= 0 || image.RawBgra.Length < (long)w * h * 4) return null;
        if (!(image.FovAngle > 0f) || image.FovAngle >= 180f) return null;
        return new DepthFrame(frame.Header.Frame, frame.Header.Timestamp, frame.SensorTransform,
                              w, h, image.FovAngle, image.RawBgra);
    }

    /// <summary>Pack a range in metres the way the depth camera encodes it, so a capture can be built
    /// from ranges rather than from pixels.</summary>
    public static void WriteRange(Span<byte> bgra, int index, double metres)
    {
        uint packed = (uint)Math.Clamp(
            Math.Round(Math.Clamp(metres, 0.0, FarPlaneMetres) / FarPlaneMetres * ColourScale),
            0.0, ColourScale);
        int i = index * 4;
        bgra[i] = (byte)(packed >> 16);         // B is the high byte
        bgra[i + 1] = (byte)(packed >> 8);      // G the middle
        bgra[i + 2] = (byte)packed;             // R the low byte
        bgra[i + 3] = 255;
    }

    /// <summary>
    /// Range in metres at a pixel, measured along the camera's optical axis rather than radially out
    /// from the lens — CARLA writes the scene's depth buffer, and its own pixel-to-world
    /// reconstruction (and this port's viewer picker) unprojects it on that assumption. Sky and
    /// anything past the far plane come back at <see cref="FarPlaneMetres"/>.
    /// </summary>
    public double RangeAt(int x, int y)
    {
        var pixels = _bgra.Span;
        int i = (y * Width + x) * 4;
        // R is the low byte, then G, then B — carla::image::ColorConverter::Depth.
        double packed = pixels[i + 2] + pixels[i + 1] * 256.0 + pixels[i] * 65536.0;
        return packed / ColourScale * FarPlaneMetres;
    }
}
