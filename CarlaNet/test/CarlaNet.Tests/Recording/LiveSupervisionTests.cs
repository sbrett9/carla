// The supervision in force is held on the CARLA server, not in the process that drives the run, so every
// client of one world reads the same truth for the same frame -- the owner's ruling of 2026-10-05: "I do
// not want two clients ever having different truth state." See
// Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/06_Truth_And_Annotation.md §8.2 and
// 04_Contracts.md §8.3b.
//
// A stand-in server here takes the change a session puts (update_supervision), holds it as the plugin
// holds it, and streams two real clients the snapshots its world observer would write from what it holds.
// The change is checked as it arrives, and each client's reading of each frame is checked against the
// other's.
using System.Net;
using System.Net.Sockets;
using CarlaNet.Tests.Sensors;
using CarlaNet.Transport;
using CarlaNet.Transport.MsgPackRpc.Server;
using CarlaNet.Types.Rpc.Supervision;
using CarlaNet.Types.Streaming;
using CarlaNet.Types.Supervision;
using static CarlaNet.Tests.Sensors.EpisodeStateRenderSetTests;
using static CarlaNet.Tests.Sensors.EpisodeStateSupervisionTests;

namespace CarlaNet.Tests.Recording;

public sealed class LiveSupervisionTests : IAsyncLifetime
{
    // Every call here returns R<T>, whose success is [[1, value]]. These reproduce that envelope.
    [MessagePackObject]
    public record struct SuccessVariant<T>([property: Key(0)] int VariantIndex, [property: Key(1)] T Value);

    [MessagePackObject]
    public record struct SuccessResponse<T>([property: Key(0)] SuccessVariant<T> Data);

    private static SuccessResponse<T> Ok<T>(T value) => new(new SuccessVariant<T>(1, value));

    // Each client subscribes to the world observer through get_episode_info; the stand-in hands the
    // first its stream and the second another, and sends every snapshot down both.
    private const uint DrivingProcessStream = 1;
    private const uint OtherProcessStream = 3;
    private const double DeltaSeconds = 0.05;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // Two lent bodies -- the escort's lead and an ambient flow vehicle -- and one parked.
    private const uint EscortBody = 21;
    private const uint AmbientBody = 23;
    private const uint ParkedBody = 24;

    private static readonly SupervisionUpdateAnnotation Lead = new(
        "Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3",
        ["bahonar:coordinated_group_transit", "bahonar:destination_off_pattern"], "transit", "bahonar:lead");

    private static readonly SupervisionUpdateAbsence Unmanned = new(
        "Shahid_Bahonar_Port_PatternOfLife/pi_tower_relief_d4_h7_t3_unmanned",
        ["bahonar:post_unmanned"], ["tower_03"], "vacancy");

    private readonly StandInStreams _streams = new(Patience);
    private readonly HeldSupervision _held = new();
    private readonly List<SupervisionUpdate> _received = [];
    private int _subscriptions;
    private MsgPackRpcServer? _rpc;
    private CarlaClient? _driving;
    private CarlaClient? _elsewhere;

    public async Task InitializeAsync()
    {
        int port = FreeLoopbackPort();
        _rpc = new MsgPackRpcServer(IPAddress.Loopback, port);
        _rpc.RegisterHandler("get_episode_info", () =>
        {
            uint stream = Interlocked.Increment(ref _subscriptions) == 1 ? DrivingProcessStream : OtherProcessStream;
            return Ok(new EpisodeInfo(1UL, new RawToken(_streams.Token(stream))));
        });
        _rpc.RegisterHandler<SupervisionUpdate, SuccessResponse<uint>>("update_supervision", update =>
        {
            lock (_received)
            {
                _received.Add(update);
                return Ok(_held.Apply(update, lent: [EscortBody, AmbientBody]));
            }
        });
        await _rpc.StartAsync();
        _driving = new CarlaClient("127.0.0.1", port, Patience);
        _elsewhere = new CarlaClient("127.0.0.1", port, Patience);
        await _driving.StartWorldObserverAsync();
        await _elsewhere.StartWorldObserverAsync();
    }

    public async Task DisposeAsync()
    {
        if (_driving is not null) await _driving.DisposeAsync();
        if (_elsewhere is not null) await _elsewhere.DisposeAsync();
        if (_rpc is not null) await _rpc.DisposeAsync();
        _streams.Dispose();
    }

    [Fact]
    public async Task A_Change_Reaches_The_Server_As_It_Was_Put()
    {
        SupervisionUpdate put = Bound(fresh: true,
                                      [new SupervisionUpdateActor(EscortBody, "annotated", [Lead])],
                                      [Unmanned], []);

        uint applied = await _driving!.UpdateSupervisionAsync(put);

        Assert.Equal(1u, applied);
        SupervisionUpdate arrived = Assert.Single(_received);
        Assert.True(arrived.Fresh);
        Assert.Equal(PlanId, arrived.PlanId);
        Assert.Equal(VocabularyVersion, arrived.VocabularyVersion);
        Assert.Equal(VocabularyDigest, arrived.VocabularyDigest);
        SupervisionUpdateActor actor = Assert.Single(arrived.Actors);
        Assert.Equal(EscortBody, actor.ActorId);
        Assert.Equal("annotated", actor.State);
        SupervisionUpdateAnnotation annotation = Assert.Single(actor.Annotations);
        Assert.Equal(Lead.InstanceId, annotation.InstanceId);
        Assert.Equal(Lead.Labels, annotation.Labels);
        Assert.Equal("transit", annotation.Phase);
        Assert.Equal("bahonar:lead", annotation.Role);
        SupervisionUpdateAbsence absence = Assert.Single(arrived.AbsencesOpened);
        Assert.Equal(Unmanned.InstanceId, absence.InstanceId);
        Assert.Equal(["tower_03"], absence.Areas);
        Assert.Equal("vacancy", absence.Phase);
        Assert.Empty(arrived.AbsencesClosed);
    }

    [Theory]
    [InlineData("anomalous", false)]   // not one of the core's three spellings
    [InlineData("Annotated", false)]   // the core's spelling is lower case
    [InlineData("annotated", true)]    // annotated with no instance named
    [InlineData("unlabelled", false)]  // unlabelled carrying an annotation
    public async Task A_Change_The_Server_Would_Refuse_Is_Refused_Before_It_Is_Sent(string state, bool bare)
    {
        SupervisionUpdate put = Bound(fresh: true,
                                      [new SupervisionUpdateActor(EscortBody, state, bare ? [] : [Lead])], [], []);

        await Assert.ThrowsAsync<ArgumentException>(() => _driving!.UpdateSupervisionAsync(put));

        // And a withdrawal carries nothing but itself.
        SupervisionUpdate withdrawal = new(false, string.Empty, 0, string.Empty, [], [Unmanned], []);
        await Assert.ThrowsAsync<ArgumentException>(() => _driving!.UpdateSupervisionAsync(withdrawal));
        Assert.Empty(_received);
    }

    [Fact]
    public async Task Every_Client_Of_The_World_Reads_The_Same_Supervision_For_The_Same_Frame()
    {
        // Frame 100: the escort's lead annotated, the ambient vehicle unlabelled, the tower unmanned.
        await _driving!.UpdateSupervisionAsync(Bound(fresh: true,
                                                     [new SupervisionUpdateActor(EscortBody, "annotated", [Lead])],
                                                     [Unmanned], []));
        await Observe(100);
        // Frame 101: the transit's interval closed and the absence with it.
        await _driving.UpdateSupervisionAsync(Bound(fresh: false,
                                                    [new SupervisionUpdateActor(EscortBody, "unlabelled", [])],
                                                    [], [Unmanned.InstanceId]));
        await Observe(101);

        foreach (CarlaClient reader in new[] { _driving, _elsewhere! })
        {
            Assert.NotNull(reader.GetSnapshotFrame(100, out ulong served, out ObservedRenderSet renderSet,
                                                   out ObservedSupervision atHundred));
            Assert.Equal(100UL, served);
            Assert.Equal(new SupervisionPlanIdentity(PlanId, (int)VocabularyVersion, VocabularyDigest), atHundred.Plan);
            IReadOnlyDictionary<string, SupervisionInForce> vehicles = atHundred.ForVehicles(renderSet);
            Assert.Equal(["corridor_d0_p0_h6.12", "escort_0"], vehicles.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(SupervisionState.Annotated, vehicles["escort_0"].State);
            Assert.Equal(Lead.InstanceId, Assert.Single(vehicles["escort_0"].Annotations).InstanceId);
            Assert.Same(SupervisionInForce.Unlabelled, vehicles["corridor_d0_p0_h6.12"]);
            Assert.Equal(Unmanned.InstanceId, Assert.Single(atHundred.Absences).InstanceId);
            // A parked body draws no vehicle, so it is no subject of the plan.
            Assert.False(vehicles.ContainsKey(string.Empty));
            Assert.True(renderSet.IsParked(ParkedBody));

            ObservedSupervision atHundredOne = reader.GetSupervisionFrame(101)!;
            Assert.Empty(atHundredOne.ByActor);
            Assert.Empty(atHundredOne.Absences);
            Assert.True(atHundredOne.IsCarried);
            Assert.Same(atHundredOne, reader.GetCachedSupervision());
            Assert.Equal(0, reader.SupervisionBlocksUnreadable);
        }

        // Read in two processes, frame for frame the same truth.
        Assert.Equal(_driving.GetSupervisionFrame(100)!.ForVehicles(_driving.GetRenderSetFrame(100)!),
                     _elsewhere!.GetSupervisionFrame(100)!.ForVehicles(_elsewhere.GetRenderSetFrame(100)!));
        Assert.Equal(_driving.GetSupervisionFrame(100)!.Absences, _elsewhere.GetSupervisionFrame(100)!.Absences);
    }

    [Fact]
    public async Task A_Withdrawal_Leaves_Every_Client_Reading_No_Supervision()
    {
        await _driving!.UpdateSupervisionAsync(Bound(fresh: true,
                                                     [new SupervisionUpdateActor(EscortBody, "annotated", [Lead])],
                                                     [Unmanned], []));
        await Observe(100);
        await _driving.UpdateSupervisionAsync(new SupervisionUpdate(false, string.Empty, 0, string.Empty, [], [], []));
        await Observe(101);

        foreach (CarlaClient reader in new[] { _driving, _elsewhere! })
        {
            Assert.True(reader.GetSupervisionFrame(100)!.IsCarried);
            Assert.Same(ObservedSupervision.None, reader.GetSupervisionFrame(101));
            Assert.Same(ObservedSupervision.None, reader.GetCachedSupervision());
        }
    }

    private static SupervisionUpdate Bound(bool fresh, IReadOnlyList<SupervisionUpdateActor> actors,
                                           IReadOnlyList<SupervisionUpdateAbsence> opened,
                                           IReadOnlyList<string> closed) =>
        new(fresh, PlanId, VocabularyVersion, VocabularyDigest, actors, opened, closed);

    /// <summary>
    /// The stand-in's world observer writes <paramref name="frame"/> from what it holds, and both clients
    /// have it before this returns.
    /// </summary>
    private async Task Observe(ulong frame)
    {
        Entry[] entries =
        [
            new(EscortBody, ObservedBodyState.Lent, 96, "escort_0", "military_truck"),
            new(AmbientBody, ObservedBodyState.Lent, 90, "corridor_d0_p0_h6.12", "car"),
            new(ParkedBody, ObservedBodyState.Parked, 0, "", ""),
        ];
        uint[] actors = [EscortBody, AmbientBody, ParkedBody];
        byte[] payload = _held.PlanId is null
            ? Snapshot(Block(entries), actors)
            : SupervisedSnapshot(RenderSetWithSupervision(entries, _held.Write()), actors);
        await _streams.SendAsync(DrivingProcessStream, frame, frame * DeltaSeconds, default, payload);
        await _streams.SendAsync(OtherProcessStream, frame, frame * DeltaSeconds, default, payload);
        await Until(() => _driving!.LatestObservedFrame == frame && _elsewhere!.LatestObservedFrame == frame,
                    $"both clients reaching frame {frame}");
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// The supervision the stand-in holds, kept as the plugin keeps it (CarlaServer.cpp's
    /// update_supervision): under one plan, a body's row only while it is lent, absences by instance, and
    /// a change naming no plan withdrawing everything; written as WorldObserver.cpp writes it.
    /// </summary>
    private sealed class HeldSupervision
    {
        private readonly Dictionary<uint, Row> _rows = [];
        private readonly List<Absence> _absences = [];

        public string? PlanId { get; private set; }

        public uint Apply(SupervisionUpdate update, HashSet<uint> lent)
        {
            if (update.PlanId.Length == 0)
            {
                PlanId = null;
                _rows.Clear();
                _absences.Clear();
                return 0;
            }

            if (update.Fresh || PlanId is null)
            {
                _rows.Clear();
                _absences.Clear();
            }

            PlanId = update.PlanId;
            uint applied = 0;
            foreach (SupervisionUpdateActor actor in update.Actors.Where(actor => lent.Contains(actor.ActorId)))
            {
                if (actor.State == "unlabelled")
                {
                    _rows.Remove(actor.ActorId);
                }
                else
                {
                    _rows[actor.ActorId] = new Row(actor.ActorId, actor.State == "annotated" ? (byte)1 : (byte)2,
                        [.. actor.Annotations.Select(annotation => new Annotation(
                            annotation.InstanceId, annotation.Phase, annotation.Role, [.. annotation.Labels]))]);
                }

                applied++;
            }

            foreach (string closed in update.AbsencesClosed)
            {
                _absences.RemoveAll(absence => absence.Instance == closed);
            }

            foreach (SupervisionUpdateAbsence opened in update.AbsencesOpened)
            {
                _absences.RemoveAll(absence => absence.Instance == opened.InstanceId);
                _absences.Add(new Absence(opened.InstanceId, opened.Phase, [.. opened.Labels], [.. opened.Areas]));
            }

            return applied;
        }

        public byte[] Write() => SupervisionBlock([.. _rows.Values], [.. _absences], PlanId!);
    }
}
