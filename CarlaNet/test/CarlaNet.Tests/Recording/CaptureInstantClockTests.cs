// A recorder names each capture's files by its wall-clock instant to the millisecond; two captures in
// one millisecond were written to the same two paths by two workers at once, and one failed.
using CarlaNet.Recording;

namespace CarlaNet.Tests.Recording;

public class CaptureInstantClockTests
{
    private static readonly DateTime Noon = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CapturesInOneMillisecondAreStampedWithSuccessiveMilliseconds()
    {
        var clock = new CaptureInstantClock();

        DateTime first = clock.Next(Noon.AddTicks(1_000));
        DateTime second = clock.Next(Noon.AddTicks(2_000));
        DateTime third = clock.Next(Noon.AddTicks(3_000));

        Assert.Equal(Noon, first);
        Assert.Equal(Noon.AddMilliseconds(1), second);
        Assert.Equal(Noon.AddMilliseconds(2), third);
    }

    [Fact]
    public void AStampIsTheCaptureInstantToTheMillisecondWhenItIsLater()
    {
        var clock = new CaptureInstantClock();
        clock.Next(Noon);

        Assert.Equal(Noon.AddMilliseconds(50), clock.Next(Noon.AddMilliseconds(50).AddTicks(9_999)));
    }

    [Fact]
    public void AWallClockThatStepsBackStillStampsLaterCaptures()
    {
        var clock = new CaptureInstantClock();
        clock.Next(Noon.AddMilliseconds(10));

        Assert.Equal(Noon.AddMilliseconds(11), clock.Next(Noon));
    }

    [Fact]
    public void TheStampsOfABurstNameDistinctFilesInCaptureOrder()
    {
        var clock = new CaptureInstantClock();
        List<string> stems = [];
        for (int capture = 0; capture < 40; capture++)
        {
            stems.Add(clock.Next(Noon).ToString("yyyy.MM.dd_HH.mm.ss.fff"));
        }

        Assert.Equal(40, stems.Distinct().Count());
        Assert.Equal(stems.OrderBy(stem => stem, StringComparer.Ordinal), stems);
    }
}
