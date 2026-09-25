using CarlaNet.Recording;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// The store the recorder asks each capture's declaration of: by the capture's own frame, and only
/// for the last few frames.
/// </summary>
public sealed class IlluminationFramesTests
{
    [Fact]
    public void AFrameIsAnsweredWithItsOwnDeclarationAndAnUnknownOneWithNothing()
    {
        var frames = new IlluminationFrames();
        Assert.Null(frames.NewestFrame);

        frames.Record(41, new IlluminationDeclaration("advance", true, true));
        frames.Record(42, new IlluminationDeclaration("advance", true, false));

        Assert.True(frames.TryGetDeclaration(41, out IlluminationDeclaration earlier));
        Assert.True(earlier.Audited);
        Assert.False(frames.TryGetDeclaration(43, out _));
        Assert.Equal(42ul, frames.NewestFrame);
        Assert.Equal(2, frames.Declared);
    }

    [Fact]
    public void OnlyTheLastFewHundredFramesAreKept()
    {
        var frames = new IlluminationFrames();
        for (ulong frame = 1; frame <= IlluminationFrames.Capacity + 10; frame++)
        {
            frames.Record(frame, new IlluminationDeclaration("freeze_at_window_start", true, true));
        }

        Assert.False(frames.TryGetDeclaration(10, out _));
        Assert.True(frames.TryGetDeclaration(11, out _));
        Assert.Equal((ulong)IlluminationFrames.Capacity + 10, frames.NewestFrame);
    }
}
