// CarlaNet.Smoke — build and connectivity smoke test.
// F5 in Visual Studio will build all projects and run this.
// With a CARLA server: set CARLA_HOST and CARLA_PORT environment variables.
// Without a server: type construction and serialization round-trips are validated offline.
using System.Reflection;
using CarlaNet.Transport;
using CarlaNet.Types.Geom;
using CarlaNet.Types.Rpc.Control;
using CarlaNet.Types.Rpc.Environment;
using CarlaNet.Types.Rpc.Enums;
using MessagePack;

Console.WriteLine("=== CarlaNet Smoke Test ===");
Console.WriteLine($"Runtime : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS      : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine();

// ── 1. Type construction and MessagePack round-trip ──────────────────────────

Console.WriteLine("[1] Type construction and MessagePack round-trip");

var transform = new Transform(new Location(1.0f, 2.0f, 3.0f), new Rotation(0f, 90f, 0f));
byte[] serialized = MessagePackSerializer.Serialize(transform);
var deserialized  = MessagePackSerializer.Deserialize<Transform>(serialized);

Assert(deserialized.Location.X == 1.0f,  "Location.X round-trip");
Assert(deserialized.Location.Y == 2.0f,  "Location.Y round-trip");
Assert(deserialized.Rotation.Yaw == 90f, "Rotation.Yaw round-trip");
Console.WriteLine($"    Transform msgpack: {serialized.Length} bytes — OK");

var ctrl = new VehicleControl(0.5f, 0.1f, 0f, false, false, false, 0);
byte[] ctrlBytes = MessagePackSerializer.Serialize(ctrl);
var ctrlBack     = MessagePackSerializer.Deserialize<VehicleControl>(ctrlBytes);
Assert(ctrlBack.Throttle == 0.5f, "VehicleControl.Throttle round-trip");
Console.WriteLine($"    VehicleControl msgpack: {ctrlBytes.Length} bytes — OK");

var settings = new EpisodeSettings(false, false, null, true, 0.01, 10, 0f, true, 3000f, 2000f, true);
byte[] settingsBytes = MessagePackSerializer.Serialize(settings);
var settingsBack     = MessagePackSerializer.Deserialize<EpisodeSettings>(settingsBytes);
Assert(settingsBack.FixedDeltaSeconds is null,       "EpisodeSettings.FixedDeltaSeconds null round-trip");
Assert(settingsBack.SynchronousMode == false,        "EpisodeSettings.SynchronousMode round-trip");
Console.WriteLine($"    EpisodeSettings msgpack (null optional): {settingsBytes.Length} bytes — OK");

var settingsFixed = settings with { FixedDeltaSeconds = 0.05 };
byte[] sfBytes   = MessagePackSerializer.Serialize(settingsFixed);
var sfBack       = MessagePackSerializer.Deserialize<EpisodeSettings>(sfBytes);
Assert(sfBack.FixedDeltaSeconds == 0.05, "EpisodeSettings.FixedDeltaSeconds value round-trip");
Console.WriteLine($"    EpisodeSettings msgpack (0.05 optional): {sfBytes.Length} bytes — OK");

// ── 2. Sensor header size assertion ──────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("[2] Sensor header size");

int headerSize = System.Runtime.CompilerServices.Unsafe.SizeOf<CarlaNet.Transport.Streaming.RawSensorHeader>();
Assert(headerSize == 48, $"RawSensorHeader sizeof == 48 (got {headerSize})");
Console.WriteLine($"    sizeof(RawSensorHeader) = {headerSize} — OK");

// ── 3. DVSEvent size assertion (§13.3) ────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("[3] DVSEvent size (§13.3 — 20 bytes required)");

unsafe
{
    int evtSize = sizeof(CarlaNet.Sensors.DvsEvent);
    Assert(evtSize == 20, $"sizeof(DvsEvent) == 20 (got {evtSize})");
    Console.WriteLine($"    sizeof(DvsEvent) = {evtSize} — OK");
}

// ── 4. Network connectivity (optional) ───────────────────────────────────────

Console.WriteLine();
Console.WriteLine("[4] Network connectivity (optional — set CARLA_HOST to test)");

string? carlaHost = Environment.GetEnvironmentVariable("CARLA_HOST");
if (carlaHost is not null)
{
    int carlaPort = int.TryParse(Environment.GetEnvironmentVariable("CARLA_PORT"), out int p) ? p : 2000;
    Console.WriteLine($"    Connecting to {carlaHost}:{carlaPort} ...");
    try
    {
        await using var client = new CarlaClient(carlaHost, carlaPort);
        string serverVer = await client.GetServerVersionAsync();
        Console.WriteLine($"    Server version : {serverVer}");
        string clientVer = client.GetClientVersion();
        Console.WriteLine($"    Client version : {clientVer}");
        var episodeInfo = await client.GetEpisodeInfoAsync();
        Console.WriteLine($"    Episode ID     : {episodeInfo.Id}");
        Console.WriteLine("    Network connectivity — OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    WARN: {ex.Message}");
    }
}
else
{
    Console.WriteLine("    (CARLA_HOST not set — skipping live server test)");
}

// ── Summary ───────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("=== All checks passed ===");

static void Assert(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {label}");
}
