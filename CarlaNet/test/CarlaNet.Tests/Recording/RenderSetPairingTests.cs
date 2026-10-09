// A capture's truth is cut to the bodies its own frame rendered, each named by the SUMO vehicle it
// rendered; a frame whose render set is no longer held lists no vehicle and is counted. See
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/06_Truth_And_Annotation.md §8.2.
using CarlaNet.Recording;

namespace CarlaNet.Tests.Recording;

public class RenderSetPairingTests
{
    /// <summary>A render-set source a test fills frame by frame, as a session does tick by tick.</summary>
    private sealed class Frames : IRenderSetSource
    {
        private readonly Dictionary<ulong, RenderSet> _byFrame = [];
        private readonly object _lock = new();

        public ulong? NewestFrame { get; private set; }

        public void Record(ulong frame, params RenderedVehicle[] vehicles)
        {
            lock (_lock)
            {
                _byFrame[frame] = new RenderSet(vehicles);
                if (NewestFrame is not { } newest || frame > newest) NewestFrame = frame;
            }
        }

        public void Forget(ulong frame)
        {
            lock (_lock) _byFrame.Remove(frame);
        }

        public bool TryGetRenderSet(ulong frame, out RenderSet renderSet)
        {
            lock (_lock) return _byFrame.TryGetValue(frame, out renderSet!);
        }
    }

    // Every vehicle actor the world holds at a frame: two bodies lent out on the road, and one parked
    // out of sight 300 m below the ground, as the pool leaves a body between loans.
    private static VehicleTelemetry Body(uint id, double hae, double speed) => new(
        id, "vehicle.fuso.mitsubishi", "truck", "", "10,20,30", "autopilot",
        38.9051631, -119.7526194, hae, 1420.9,
        speed, 90.0, speed, 0.0, 0.0,
        7.0, 2.5, 3.2);

    private static readonly VehicleTelemetry[] World =
    [
        Body(5, 1421.6, 12.5),
        Body(6, 1422.1, 8.0),
        Body(8, 1120.8, 0.0),
    ];

    private static string Sidecar(PairedTruth paired, ulong frame)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        try
        {
            CotWriter.WriteToFile(path, new DateTime(2026, 9, 28, 21, 3, 8, DateTimeKind.Utc), paired.Records,
                                  capture: new CaptureIdentity(frame, 353.9, "run-1"),
                                  vehicles: paired.Vehicles);
            return File.ReadAllText(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_Sidecar_Lists_Exactly_Its_Own_Frame_S_Rendered_Set_Even_When_The_Next_Frame_Changes_Hands()
    {
        // Between frames 100 and 101 the escort leaves the render set and its body is lent to a
        // corridor vehicle; body 8 stays parked throughout. Frame 101 is recorded before frame 100's
        // capture is paired, as it is whenever an image arrives a tick after its frame.
        var frames = new Frames();
        frames.Record(100, new RenderedVehicle(5, "escort_0", "escort", 60),
                           new RenderedVehicle(6, "corridor_d0_p0_h6.12", "corridor", 97));
        frames.Record(101, new RenderedVehicle(5, "corridor_d0_p1_h6.40", "corridor", 101),
                           new RenderedVehicle(6, "corridor_d0_p0_h6.12", "corridor", 97));
        var pairing = new RenderSetPairing(frames);

        PairedTruth atHundred = pairing.Pair(World, 100);
        Assert.Equal(SidecarVehicles.Rendered, atHundred.Vehicles);
        Assert.Equal(new uint[] { 5, 6 }, atHundred.Records.Select(record => record.Id).Order());
        Assert.Equal("escort_0", atHundred.Records.Single(record => record.Id == 5).Rendered!.SumoId);

        string xml = Sidecar(atHundred, 100);
        Assert.Contains("count=\"2\"", xml);
        Assert.Contains("vehicles=\"rendered\"", xml);
        Assert.Contains("uid=\"CARLA-TRUTH-SUMO-escort_0\"", xml);
        Assert.Contains("callsign=\"truck-escort_0\"", xml);
        Assert.Contains("actor_id=\"5\"", xml);
        Assert.Contains("sumo_id=\"escort_0\" vtype_id=\"escort\" admitted_tick=\"60\"", xml);
        Assert.Contains("uid=\"CARLA-TRUTH-SUMO-corridor_d0_p0_h6.12\"", xml);
        Assert.DoesNotContain("corridor_d0_p1_h6.40", xml);
        // The parked body is not a vehicle in the scene, and no event says it is.
        Assert.DoesNotContain("actor_id=\"8\"", xml);
        Assert.DoesNotContain("hae=\"1120.80\"", xml);
        // No uid is built on an actor id once the vehicle is named.
        Assert.DoesNotContain("uid=\"CARLA-TRUTH-5\"", xml);

        // The next frame's body 5 is the corridor vehicle it was lent to, not the escort.
        PairedTruth atHundredAndOne = pairing.Pair(World, 101);
        Assert.Equal("corridor_d0_p1_h6.40", atHundredAndOne.Records.Single(record => record.Id == 5).Rendered!.SumoId);
        Assert.Contains("uid=\"CARLA-TRUTH-SUMO-corridor_d0_p1_h6.40\"", Sidecar(atHundredAndOne, 101));

        Assert.Equal(2, pairing.Paired);
        Assert.Equal(0, pairing.Unpaired);
        Assert.Equal(0, pairing.BodiesMissing);
    }

    [Fact]
    public void A_Frame_Whose_Set_Has_Aged_Out_Is_Written_With_No_Vehicles_Rather_Than_A_Guess_And_Counted()
    {
        // The source has moved on past frame 100 and no longer holds it. Its neighbours are held, and
        // either would be a guess; the whole world's actor list would be the parked body listed as a
        // vehicle.
        var frames = new Frames();
        frames.Record(99, new RenderedVehicle(5, "escort_0", "escort", 60));
        frames.Record(100, new RenderedVehicle(5, "escort_0", "escort", 60));
        frames.Record(101, new RenderedVehicle(5, "corridor_d0_p1_h6.40", "corridor", 101));
        frames.Forget(100);
        var pairing = new RenderSetPairing(frames);

        PairedTruth paired = pairing.Pair(World, 100);

        Assert.Equal(SidecarVehicles.Unknown, paired.Vehicles);
        Assert.Empty(paired.Records);
        Assert.Equal(0, pairing.Paired);
        Assert.Equal(1, pairing.Unpaired);

        // Said on the sidecar, so an empty list is never read as an empty scene.
        string xml = Sidecar(paired, 100);
        Assert.Contains("count=\"0\"", xml);
        Assert.Contains("vehicles=\"unknown\"", xml);
        Assert.DoesNotContain("CARLA-TRUTH", xml);
    }

    [Fact]
    public void A_Frame_The_Source_Never_Reaches_Is_Given_Up_On_After_A_Bounded_Wait()
    {
        var frames = new Frames();
        var pairing = new RenderSetPairing(frames, TimeSpan.FromMilliseconds(20));

        PairedTruth paired = pairing.Pair(World, 100);

        Assert.Equal(SidecarVehicles.Unknown, paired.Vehicles);
        Assert.Equal(1, pairing.Unpaired);
    }

    [Fact]
    public void A_Capture_That_Arrives_Before_Its_Frame_Is_Recorded_Waits_For_It()
    {
        var frames = new Frames();
        frames.Record(99, new RenderedVehicle(6, "corridor_d0_p0_h6.12", "corridor", 97));
        var pairing = new RenderSetPairing(frames, TimeSpan.FromSeconds(10));

        Task recorded = Task.Run(async () =>
        {
            await Task.Delay(50);
            frames.Record(100, new RenderedVehicle(5, "escort_0", "escort", 100));
        });
        PairedTruth paired = pairing.Pair(World, 100);
        recorded.Wait();

        Assert.Equal(SidecarVehicles.Rendered, paired.Vehicles);
        Assert.Equal("escort_0", Assert.Single(paired.Records).Rendered!.SumoId);
        Assert.Equal(1, pairing.Paired);
    }

    [Fact]
    public void A_Rendered_Body_With_No_Truth_Record_Is_Counted()
    {
        var frames = new Frames();
        frames.Record(100, new RenderedVehicle(5, "escort_0", "escort", 60),
                           new RenderedVehicle(9, "corridor_d0_p2_h6.55", "corridor", 98));
        var pairing = new RenderSetPairing(frames);

        PairedTruth paired = pairing.Pair(World, 100);

        Assert.Equal(new uint[] { 5 }, paired.Records.Select(record => record.Id));
        Assert.Equal(1, pairing.BodiesMissing);
    }

    [Fact]
    public void A_Parked_Body_Is_Not_Measured_And_Hides_No_Rendered_Vehicle()
    {
        // Looking along +X over ground that the depth capture reads at 20 m in every pixel: a rendered
        // vehicle stands in the open at 10 m, and a parked body is beyond the ground surface at 30 m,
        // where nothing the camera draws can reach it -- the 300 m below ground of a real parking slot,
        // folded into one axis.
        const int size = 200;
        var bgra = new byte[size * size * 4];
        for (int index = 0; index < size * size; index++)
            DepthFrame.WriteRange(bgra, index, 20.0, DepthFrame.DefaultMaxRangeMetres);
        var camera = new Transform(new Location(0f, 0f, 0f), new Rotation(0f, 0f, 0f));
        var depth = new DepthFrame(100, 353.9, camera, size, size, 90.0, bgra, DepthFrame.DefaultMaxRangeMetres);
        var box = new BoundingBox(new Location(0f, 0f, 0f), new Vector3D(2f, 1f, 0.75f), new Rotation());
        VehicleTelemetry rendered = Body(5, 1421.6, 12.5) with
        {
            ActorTransform = new Transform(new Location(10f, 0f, 0f), new Rotation()), BoundingBox = box,
        };
        VehicleTelemetry parked = Body(8, 1120.8, 0.0) with
        {
            ActorTransform = new Transform(new Location(30f, 0f, 0f), new Rotation()), BoundingBox = box,
        };

        static IReadOnlyDictionary<uint, VehicleOcclusion> Measure(DepthFrame depth, IEnumerable<VehicleTelemetry> records) =>
            OcclusionEstimator.Estimate(
                depth, records.Select(r => new VehicleBox(r.Id, r.ActorTransform, r.BoundingBox)).ToList(),
                OcclusionOptions.Default);

        // Measured unpaired, the parked body is a phantom vehicle reported wholly hidden.
        IReadOnlyDictionary<uint, VehicleOcclusion> unpaired = Measure(depth, [rendered, parked]);
        Assert.Equal(1.0, unpaired[8].Fraction, 6);

        // Paired, it is not measured at all, and the rendered vehicle's measurement is the same with
        // or without it: an occluder is only ever what the depth capture drew.
        var frames = new Frames();
        frames.Record(100, new RenderedVehicle(5, "escort_0", "escort", 60));
        PairedTruth paired = new RenderSetPairing(frames).Pair([rendered, parked], 100);
        IReadOnlyDictionary<uint, VehicleOcclusion> measured = Measure(depth, paired.Records);
        Assert.Equal(new uint[] { 5 }, measured.Keys);
        Assert.Equal(0.0, measured[5].Fraction, 6);
        Assert.Equal(unpaired[5], measured[5]);
    }
}
