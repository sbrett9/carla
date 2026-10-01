// The snapshot history is what lets a still be paired with the actor state of its own frame instead
// of the newest one. A camera image arrives some ticks after the snapshot of its frame, so "newest"
// was routinely a tick or more ahead of the pixels; these tests pin the lookup that replaces it.
using CarlaNet.Transport;
using CarlaNet.Types.Streaming;

namespace CarlaNet.Tests.Transport;

public class SnapshotHistoryTests
{
    private static IReadOnlyDictionary<uint, ActorSnapshot> Frame(params uint[] ids)
    {
        var d = new Dictionary<uint, ActorSnapshot>();
        foreach (var id in ids) d[id] = new ActorSnapshot { Id = id };
        return d;
    }

    [Fact]
    public void Nothing_Retained_Answers_Null()
    {
        var h = new SnapshotHistory();
        Assert.Null(h.Nearest(10, out ulong served));
        Assert.Equal(0ul, served);
        Assert.Equal(0, h.Count);
        Assert.Null(h.NewestFrame);
    }

    [Fact]
    public void A_Held_Frame_Is_Served_Exactly()
    {
        var h = new SnapshotHistory();
        h.Retain(100, Frame(1, 2));
        h.Retain(101, Frame(1, 2, 3));
        h.Retain(102, Frame(2, 3));

        var at101 = h.Nearest(101, out ulong served);
        Assert.Equal(101ul, served);
        Assert.NotNull(at101);
        Assert.Equal(new uint[] { 1, 2, 3 }, at101!.Keys.OrderBy(k => k));
        Assert.True(h.Holds(101));
        Assert.Equal(102ul, h.NewestFrame);
    }

    [Fact]
    public void A_Frame_Not_Held_Gets_The_Nearest_One_And_Says_So()
    {
        var h = new SnapshotHistory();
        h.Retain(100, Frame(1));
        h.Retain(104, Frame(2));

        // 103 is one frame from 104 and three from 100.
        var got = h.Nearest(103, out ulong served);
        Assert.Equal(104ul, served);
        Assert.Contains(2u, got!.Keys);

        Assert.Equal(100ul, ServedFor(h, 90));
        Assert.Equal(104ul, ServedFor(h, 900));
        Assert.False(h.Holds(103));
    }

    [Fact]
    public void The_Oldest_Frame_Goes_When_Over_Capacity()
    {
        var h = new SnapshotHistory(capacity: 3);
        for (ulong f = 1; f <= 5; f++) h.Retain(f, Frame((uint)f));

        Assert.Equal(3, h.Count);
        Assert.False(h.Holds(1));
        Assert.False(h.Holds(2));
        Assert.True(h.Holds(3));
        Assert.True(h.Holds(5));
        // A request for an evicted frame is answered by the oldest survivor, and named as such.
        Assert.Equal(3ul, ServedFor(h, 1));
    }

    [Fact]
    public void Retaining_A_Frame_Again_Replaces_It_Without_Counting_Twice()
    {
        var h = new SnapshotHistory(capacity: 2);
        h.Retain(7, Frame(1));
        h.Retain(7, Frame(1, 2));
        h.Retain(8, Frame(3));

        Assert.Equal(2, h.Count);
        Assert.Equal(2, h.Nearest(7, out _)!.Count);
        Assert.True(h.Holds(7));
    }

    [Fact]
    public void Default_Capacity_Covers_The_Longest_Delivery_Seen()
    {
        // Seven ticks was the worst image delivery measured; the default keeps far more than that.
        Assert.True(SnapshotHistory.DefaultCapacity >= 32);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotHistory(0));
    }

    [Fact]
    public void Clear_Forgets_Everything()
    {
        var h = new SnapshotHistory();
        h.Retain(1, Frame(1));
        h.Clear();
        Assert.Equal(0, h.Count);
        Assert.Null(h.Nearest(1, out _));
    }

    // A SUMO drive's render set travels with each snapshot, and a frame's actors are only ever read
    // with the set that same frame carried: a body lent or given back between two ticks would
    // otherwise be read at one frame's pose under the other frame's naming.
    private static ObservedRenderSet Lent(uint actor, string vehicle) =>
        new([new ObservedBody(actor, ObservedBodyState.Lent, vehicle, "car", 1)]);

    [Fact]
    public void A_Frame_s_Actors_Are_Served_With_The_Render_Set_That_Frame_Carried()
    {
        var h = new SnapshotHistory();
        ObservedRenderSet first = Lent(7, "first"), second = Lent(7, "second");
        h.Retain(100, Frame(7), first);
        h.Retain(104, Frame(7), second);

        Assert.NotNull(h.Nearest(100, out ulong exact, out ObservedRenderSet atExact));
        Assert.Equal(100ul, exact);
        Assert.Same(first, atExact);

        // A frame not held is served from the nearest one, and with that frame's set, not its own.
        Assert.NotNull(h.Nearest(103, out ulong served, out ObservedRenderSet atServed));
        Assert.Equal(104ul, served);
        Assert.Same(second, atServed);

        Assert.Same(first, h.RenderSetOf(100));
        Assert.Null(h.RenderSetOf(103));
    }

    [Fact]
    public void A_Frame_Retained_Without_A_Render_Set_Carries_None_And_Leaves_With_Its_Actors()
    {
        var h = new SnapshotHistory(capacity: 2);
        h.Retain(1, Frame(1));
        h.Retain(2, Frame(1), Lent(1, "escort_0"));
        h.Retain(3, Frame(1), Lent(1, "escort_1"));

        Assert.Null(h.RenderSetOf(1));                              // dropped with frame 1
        Assert.Equal("escort_0", h.RenderSetOf(2)!.Lent(1)!.VehicleId);
        Assert.Null(new SnapshotHistory().Nearest(5, out _, out ObservedRenderSet none));
        Assert.Same(ObservedRenderSet.None, none);

        var unnamed = new SnapshotHistory();
        unnamed.Retain(9, Frame(1));
        Assert.Same(ObservedRenderSet.None, unnamed.RenderSetOf(9));
    }

    private static ulong ServedFor(SnapshotHistory h, ulong frame)
    {
        h.Nearest(frame, out ulong served);
        return served;
    }
}
