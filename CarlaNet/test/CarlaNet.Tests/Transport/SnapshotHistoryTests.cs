// The snapshot history is what lets a still be paired with the actor state of its own frame instead
// of the newest one. A camera image arrives some ticks after the snapshot of its frame, so "newest"
// was routinely a tick or more ahead of the pixels; these tests pin the lookup that replaces it: a
// frame is served exactly or not at all, and a frame is kept for as long as a reader holding the
// history may still ask for it (the owner's ruling of 2026-10-05: a still is never written with
// another frame's truth).
using CarlaNet.Transport;
using CarlaNet.Types.Streaming;
using CarlaNet.Types.Supervision;

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
        Assert.Null(h.Of(10));
        Assert.Equal(0, h.Count);
        Assert.Null(h.NewestFrame);
        Assert.Null(h.OldestFrame);
    }

    [Fact]
    public void A_Held_Frame_Is_Served_Exactly()
    {
        var h = new SnapshotHistory();
        h.Retain(100, Frame(1, 2));
        h.Retain(101, Frame(1, 2, 3));
        h.Retain(102, Frame(2, 3));

        var at101 = h.Of(101);
        Assert.NotNull(at101);
        Assert.Equal(new uint[] { 1, 2, 3 }, at101!.Keys.OrderBy(k => k));
        Assert.True(h.Holds(101));
        Assert.Equal(102ul, h.NewestFrame);
        Assert.Equal(100ul, h.OldestFrame);
    }

    [Fact]
    public void A_Frame_Not_Held_Is_Not_Served_From_A_Neighbour()
    {
        var h = new SnapshotHistory();
        h.Retain(100, Frame(1));
        h.Retain(104, Frame(2));

        // 103 is one frame from 104: another instant's actors, and not an answer for 103.
        Assert.Null(h.Of(103));
        Assert.Null(h.Of(90));
        Assert.Null(h.Of(900));
        Assert.False(h.Holds(103));
        Assert.Null(h.Of(103, out ObservedRenderSet none));
        Assert.Same(ObservedRenderSet.None, none);
    }

    [Fact]
    public void With_No_Hold_Open_Only_The_Newest_Few_Frames_Are_Kept()
    {
        var h = new SnapshotHistory(idleFrames: 3);
        for (ulong f = 1; f <= 5; f++) h.Retain(f, Frame((uint)f));

        Assert.Equal(3, h.Count);
        Assert.False(h.Holds(1));
        Assert.False(h.Holds(2));
        Assert.True(h.Holds(3));
        Assert.True(h.Holds(5));
        Assert.Null(h.Of(1));
        Assert.Equal(0, h.OpenHolds);
    }

    [Fact]
    public void A_Hold_Keeps_Every_Frame_Until_The_Holder_Releases_It()
    {
        // A recorder's hold: the frames in flight when it opens, and every frame after, are kept past
        // the idle bound until the holder says which it has finished with.
        var h = new SnapshotHistory(idleFrames: 3);
        h.Retain(1, Frame(1));
        h.Retain(2, Frame(2));
        using SnapshotHold hold = h.Hold();
        for (ulong f = 3; f <= 40; f++) h.Retain(f, Frame((uint)f));

        Assert.Equal(1, h.OpenHolds);
        Assert.Equal(40, h.Count);
        Assert.True(h.Holds(1));
        Assert.Null(hold.Floor);

        // The holder has paired an image of frame 30 and keeps a margin of four behind it.
        hold.Release(26);
        Assert.Equal(26ul, hold.Floor);
        Assert.Equal(15, h.Count);
        Assert.False(h.Holds(25));
        Assert.True(h.Holds(26));
        Assert.True(h.Holds(40));

        // A release below the last changes nothing: frames are finished with in order.
        hold.Release(10);
        Assert.Equal(26ul, hold.Floor);
        Assert.Equal(15, h.Count);
    }

    [Fact]
    public void The_Lowest_Floor_Of_Several_Holds_Is_The_One_That_Binds()
    {
        // Two recorders on one client, one of them lagging: nothing the slower one may still ask for
        // is dropped for the faster one's progress.
        var h = new SnapshotHistory(idleFrames: 2);
        using SnapshotHold fast = h.Hold();
        using SnapshotHold slow = h.Hold();
        for (ulong f = 1; f <= 20; f++) h.Retain(f, Frame((uint)f));

        fast.Release(18);
        Assert.Equal(20, h.Count);              // the slow hold has released nothing yet
        slow.Release(10);
        Assert.Equal(11, h.Count);              // frames 10 to 20
        Assert.True(h.Holds(10));
        Assert.False(h.Holds(9));

        slow.Dispose();
        Assert.Equal(3, h.Count);               // the fast hold's floor, 18 to 20
        Assert.True(h.Holds(18));
    }

    [Fact]
    public void Closing_The_Last_Hold_Returns_To_The_Idle_Bound()
    {
        var h = new SnapshotHistory(idleFrames: 2);
        SnapshotHold hold = h.Hold();
        for (ulong f = 1; f <= 6; f++) h.Retain(f, Frame((uint)f));
        Assert.Equal(6, h.Count);

        hold.Dispose();
        Assert.Equal(0, h.OpenHolds);
        Assert.Equal(2, h.Count);
        Assert.True(h.Holds(5));
        Assert.True(h.Holds(6));

        // A closed hold releases nothing and is not counted twice.
        hold.Release(6);
        hold.Dispose();
        Assert.Equal(2, h.Count);
    }

    [Fact]
    public void The_Capacity_Bounds_A_Holder_That_Has_Stopped_Releasing()
    {
        // A recorder whose camera has stopped delivering while the world ticks on would otherwise pin
        // every frame; the capacity drops the oldest, and an image of one of those is refused, not
        // served from a neighbour.
        var h = new SnapshotHistory(capacity: 3, idleFrames: 3);
        using SnapshotHold hold = h.Hold();
        for (ulong f = 1; f <= 5; f++) h.Retain(f, Frame((uint)f));

        Assert.Equal(3, h.Count);
        Assert.False(h.Holds(1));
        Assert.Null(h.Of(1));
        Assert.True(h.Holds(3));
    }

    [Fact]
    public void Retaining_A_Frame_Again_Replaces_It_Without_Counting_Twice()
    {
        var h = new SnapshotHistory(idleFrames: 2);
        h.Retain(7, Frame(1));
        h.Retain(7, Frame(1, 2));
        h.Retain(8, Frame(3));

        Assert.Equal(2, h.Count);
        Assert.Equal(2, h.Of(7)!.Count);
        Assert.True(h.Holds(7));
    }

    [Fact]
    public void The_Bounds_Cover_The_Longest_Delivery_Seen_And_Refuse_Nothing_Kept()
    {
        // Seven ticks was the worst image delivery measured; the idle bound keeps more than that, so
        // an image in flight when a recorder opens its hold still finds its frame. The capacity is
        // what a stalled holder can pin at most, and it is above any delivery seen.
        Assert.True(SnapshotHistory.DefaultIdleFrames > 7);
        Assert.True(SnapshotHistory.DefaultCapacity >= 4 * SnapshotHistory.DefaultIdleFrames);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotHistory(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotHistory(idleFrames: 0));
        // An idle bound above the capacity is the capacity.
        Assert.Equal(4, new SnapshotHistory(capacity: 4, idleFrames: 10).IdleFrames);
    }

    [Fact]
    public void Clear_Forgets_Everything()
    {
        var h = new SnapshotHistory();
        h.Retain(1, Frame(1));
        h.Clear();
        Assert.Equal(0, h.Count);
        Assert.Null(h.Of(1));
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

        Assert.NotNull(h.Of(100, out ObservedRenderSet atExact));
        Assert.Same(first, atExact);
        Assert.NotNull(h.Of(104, out ObservedRenderSet atLater));
        Assert.Same(second, atLater);

        // A frame not held is served from no neighbour, with no neighbour's set.
        Assert.Null(h.Of(103, out ObservedRenderSet atMissing));
        Assert.Same(ObservedRenderSet.None, atMissing);

        Assert.Same(first, h.RenderSetOf(100));
        Assert.Null(h.RenderSetOf(103));
    }

    [Fact]
    public void A_Frame_Retained_Without_A_Render_Set_Carries_None_And_Leaves_With_Its_Actors()
    {
        var h = new SnapshotHistory(idleFrames: 2);
        h.Retain(1, Frame(1));
        h.Retain(2, Frame(1), Lent(1, "escort_0"));
        h.Retain(3, Frame(1), Lent(1, "escort_1"));

        Assert.Null(h.RenderSetOf(1));                              // dropped with frame 1
        Assert.Equal("escort_0", h.RenderSetOf(2)!.Lent(1)!.VehicleId);
        Assert.Null(new SnapshotHistory().Of(5, out ObservedRenderSet none));
        Assert.Same(ObservedRenderSet.None, none);

        var unnamed = new SnapshotHistory();
        unnamed.Retain(9, Frame(1));
        Assert.Same(ObservedRenderSet.None, unnamed.RenderSetOf(9));
    }

    // The supervision travels with each snapshot too, and names bodies as the render set does, so a
    // frame's actors, set and supervision are only ever read together, from one frame.
    private static ObservedSupervision Annotated(uint actor, string instance) =>
        new(new SupervisionPlanIdentity("plan", 2, "digest"),
            [KeyValuePair.Create(actor, new SupervisionInForce(SupervisionState.Annotated,
                                                               [new AnnotationInForce(instance, ["ns:term"], "dwell", "subject")]))],
            []);

    [Fact]
    public void A_Frame_s_Actors_Are_Served_With_The_Supervision_That_Frame_Carried()
    {
        var h = new SnapshotHistory();
        ObservedSupervision first = Annotated(7, "plan/first"), second = Annotated(7, "plan/second");
        h.Retain(100, Frame(7), Lent(7, "first"), first);
        h.Retain(104, Frame(7), Lent(7, "second"), second);

        Assert.NotNull(h.Of(100, out ObservedRenderSet setAtExact, out ObservedSupervision atExact));
        Assert.Equal("first", setAtExact.Lent(7)!.VehicleId);
        Assert.Same(first, atExact);

        Assert.NotNull(h.Of(104, out ObservedRenderSet setAtLater, out ObservedSupervision atLater));
        Assert.Equal("second", setAtLater.Lent(7)!.VehicleId);
        Assert.Same(second, atLater);

        // A frame not held is served from no neighbour, with no neighbour's supervision or set.
        Assert.Null(h.Of(103, out ObservedRenderSet setAtMissing, out ObservedSupervision atMissing));
        Assert.Same(ObservedRenderSet.None, setAtMissing);
        Assert.Same(ObservedSupervision.None, atMissing);

        Assert.Same(first, h.SupervisionOf(100));
        Assert.Null(h.SupervisionOf(103));
    }

    [Fact]
    public void A_Frame_Retained_Without_Supervision_Carries_None_And_Leaves_With_Its_Actors()
    {
        var h = new SnapshotHistory(idleFrames: 2);
        h.Retain(1, Frame(1), Lent(1, "escort_0"), Annotated(1, "plan/a"));
        h.Retain(2, Frame(1), Lent(1, "escort_0"));
        h.Retain(3, Frame(1), Lent(1, "escort_0"), Annotated(1, "plan/b"));

        Assert.Null(h.SupervisionOf(1));                              // dropped with frame 1
        Assert.Same(ObservedSupervision.None, h.SupervisionOf(2));
        Assert.Equal("plan/b", h.SupervisionOf(3)!.Of(1)!.Annotations[0].InstanceId);
        Assert.Null(new SnapshotHistory().Of(5, out _, out ObservedSupervision none));
        Assert.Same(ObservedSupervision.None, none);
    }
}
