using CarlaNet.Recording;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The store the recorder asks each capture's render set of: by the frame the capture's truth
/// describes, and only for the last few hundred frames.
/// </summary>
public sealed class RenderSetFramesTests
{
    private static RenderSet Lent(uint actor, string vehicle, ulong since) =>
        new([new RenderedVehicle(actor, vehicle, "passenger", since)]);

    [Fact]
    public void AFrameIsAnsweredWithItsOwnSetAndAnUnknownOneWithNothing()
    {
        var frames = new RenderSetFrames();
        Assert.Null(frames.NewestFrame);

        frames.Record(41, Lent(7, "escort_0", 30));
        frames.Record(42, Lent(7, "corridor_d0_p0_h6.12", 42));

        Assert.True(frames.TryGetRenderSet(41, out RenderSet earlier));
        Assert.Equal("escort_0", earlier.ByActor[7].SumoId);
        Assert.True(frames.TryGetRenderSet(42, out RenderSet later));
        Assert.Equal("corridor_d0_p0_h6.12", later.ByActor[7].SumoId);
        Assert.False(frames.TryGetRenderSet(43, out _));
        Assert.Equal(42ul, frames.NewestFrame);
        Assert.Equal(2, frames.Recorded);
    }

    [Fact]
    public void OnlyTheLastFewHundredFramesAreKept()
    {
        var frames = new RenderSetFrames();
        RenderSet unchanged = Lent(7, "escort_0", 1);
        for (ulong frame = 1; frame <= RenderSetFrames.Capacity + 10; frame++)
        {
            frames.Record(frame, unchanged);
        }

        Assert.False(frames.TryGetRenderSet(10, out _));
        Assert.True(frames.TryGetRenderSet(11, out RenderSet kept));
        Assert.Same(unchanged, kept);
        Assert.Equal((ulong)RenderSetFrames.Capacity + 10, frames.NewestFrame);
    }
}
