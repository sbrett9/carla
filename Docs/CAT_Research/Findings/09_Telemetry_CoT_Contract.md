# Telemetry CoT Contract (v0) — the shared truth/detection schema

**Date:** 2026-06-12 · **Status:** v0 AGREED 2026-06-12 (decisions in §8); truth producer implementing.
**Decided:** Option **B** (pull helper + CoT formatter + reference WinTAK emitter) — see
[../Discussions/2026-06-12_Telemetry_Serving_Options.md](../Discussions/2026-06-12_Telemetry_Serving_Options.md).
**Datum:** ellipsoidal WGS84 (HAE) — `project_datum_decision`.

## 1. Purpose

One CoT event schema emitted by **both** producers so they are directly comparable in WinTAK and in a
scoring harness:
- **TRUTH** — pulled from CARLA (exact): vehicle actor → `Geodesy.CarlaLocalToGeodetic` → CoT.
- **DETECTION** (future YOLO) — pixels → 2D bbox → depth pixel→world (the `eo_observer` Ctrl+LMB math) →
  `Geodesy` → CoT.

Same shape ⇒ truth-vs-detection scoring is a direct diff (position error, class confusion, miss/false).

## 2. Event schema (annotated)

```xml
<event version="2.0"
       uid="CARLA-TRUTH-<actor_id>"          <!-- stable per track; DETECTION uses CARLA-DET-<track_id> -->
       type="a-n-G-E-V"                       <!-- 2525 CoT type; see §4 -->
       how="m-g"                              <!-- machine / GPS-derived -->
       time="2026-06-12T18:00:00.000Z"        <!-- generated at (UTC, ms) -->
       start="2026-06-12T18:00:00.000Z"       <!-- valid from -->
       stale="2026-06-12T18:00:03.000Z">      <!-- ages off after STALE_SECONDS (default 3 s) -->
  <point lat="37.7841234" lon="-122.4567890"
         hae="61.2"                            <!-- ELLIPSOIDAL height m (our datum) -->
         ce="0.0" le="0.0"/>                   <!-- circular/linear error m; TRUTH = 0 (exact) -->
  <detail>
    <track course="182.4" speed="11.3"/>       <!-- deg true (0-360), m/s -->
    <contact callsign="car-123"/>
    <_carla source="truth" actor_id="123"
            type_id="vehicle.audi.tt" base_type="car" special_type=""
            length_m="4.5" width_m="2.0" height_m="1.4"
            color="0,0,0" role_name="autopilot"
            vx="11.2" vy="-1.4" vz="0.0"
            occlusion="0.420" occlusion_level="2" occlusion_samples="96"
            apparent_width_px="48" apparent_height_px="21"/> <!-- truth extras; WinTAK ignores unknown detail -->
  </detail>
</event>
```

## 3. Field conventions

| Field | Convention |
|---|---|
| `version` | CoT `2.0` |
| `uid` | stable per (source, track). TRUTH: `CARLA-TRUTH-<actor_id>`. DETECTION: `CARLA-DET-<track_id>`. (Scoring associates truth↔detection by position/time, **not** uid.) |
| `type` | 2525 CoT atom type — §4 |
| `how` | TRUTH `m-g` (machine/GPS). DETECTION `m-f` (machine/fused) so the provenance differs. |
| `time`/`start` | generation instant, ISO-8601 UTC ("Zulu"), millisecond precision |
| `stale` | `time + STALE_SECONDS` (default **3 s** = 15 missed updates at 5 Hz) |
| `point.lat`/`lon` | WGS84 degrees |
| `point.hae` | **ellipsoidal** height, metres (matches datum; = ground sample + local Z) |
| `point.ce`/`le` | error metres. **TRUTH = 0.0** (exact). DETECTION = estimated. (CoT "unknown" sentinel 9999999 is NOT used.) |
| `track.course` | heading **degrees true north, 0–360**. Course-over-ground from velocity: `bearing = atan2(East, North) = atan2(vx, -vy)` (CARLA +X=East, −Y=North); fall back to vehicle yaw below a speed threshold. *Verify empirically (drive north ⇒ ~0°), as with the pick math.* |
| `track.speed` | horizontal ground speed `sqrt(vx²+vy²)`, **m/s** |
| `contact.callsign` | human-readable; default `<base_type>-<actor_id>` (e.g. `car-123`) |

## 4. 2525 / CoT `type` mapping

CoT atom type format: `a-{affiliation}-G-E-V[-subtype]` — atom / standard-identity / **G**round /
**E**quipment / **V**ehicle.

- **Affiliation** `{affiliation}`: `f` friend · `h` hostile · `n` neutral · `u` unknown · `p` pending · … .
  Default below is an **open decision (§8)**; per-vehicle override (from `role_name` or a scenario map)
  is supported regardless of the default.
- **v0 (recommended): one symbol for every vehicle — `a-{aff}-G-E-V`** ("ground equipment vehicle"). The
  fine classification still rides along in `_carla` (`base_type`, `type_id`), so nothing is lost and we
  avoid shipping imperfect subtype SIDCs.

**v1 (later) per-class subtypes** — to refine against an authoritative 2525 table before use:

| CARLA `base_type` / `special_type` | candidate CoT type | note |
|---|---|---|
| `car` | `a-{aff}-G-E-V-C` | civilian vehicle |
| `truck` | `a-{aff}-G-E-V-U-T` | utility / truck (verify SIDC) |
| `van` | `a-{aff}-G-E-V-C` | treat as civilian vehicle |
| `motorcycle` / `bicycle` (2 wheels) | `a-{aff}-G-E-V-m` | motorcycle (verify SIDC) |
| `bus` | `a-{aff}-G-E-V-U-B` | bus (verify SIDC) |
| `special_type=emergency` (fire/ambulance/police) | emergency-management symbol | distinct hierarchy; refine |

Vehicle class source: CARLA blueprint attributes `base_type` (car/truck/van/motorcycle/bicycle) and
`special_type` (emergency/taxi/electric) when present; else infer from `number_of_wheels` / `type_id`.

## 5. The `_carla` truth-extras block (custom `<detail>` child)

Carries the richer-than-ADS-B fields for the scoring harness / any TAK plugin; WinTAK ignores unknown
detail children. Attributes: `source` (`truth`|`detection`), `actor_id`, `type_id`, `base_type`,
`special_type`, `length_m`/`width_m`/`height_m` (from `bounding_box.extent × 2`), `color`, `role_name`,
raw `vx`/`vy`/`vz`. (Detection fills what it can: `source="detection"`, confidence, predicted class.)

### 5.1 Occlusion (recorded captures only)

| Attribute | Meaning |
|---|---|
| `occlusion` | Fraction of the vehicle's silhouette hidden from **this capture's camera** by anything nearer — photoreal buildings and trees, terrain relief, other vehicles — 0 (wholly visible) to 1 (wholly hidden). |
| `occlusion_level` | The same value as a coarse band: `0` wholly visible · `1` up to 30 % · `2` 30–60 % · `3` 60–90 % · `4` over 90 % (the bands the amodal-segmentation datasets report against). |
| `occlusion_samples` | How many points across the vehicle's outline the fraction was measured over. |
| `apparent_width_px`, `apparent_height_px` | How large the vehicle appears in the frame — its full projected footprint, including any part outside the frame. |

**Read the fraction against the sample count.** A vehicle far enough away to cover a few pixels yields
a few samples, and can then only report coarse values — a half, a third — however many decimal places
it is written to. Measured at ~1.1 km with a 90° camera, vehicles are about 3 px long and every
reported fraction is a simple ratio, where vehicles inside 150 m give fine-grained values. The
apparent size is also the natural gate for whether a box is worth drawing at all: a three-pixel
vehicle is a poor training example whether or not anything is in front of it.

Occlusion is **camera-relative** — a property of the (vehicle, sensor) pair, not of the vehicle — so
it is only emitted in the recorded sidecar, where the frame already carries a sensor pose (16), and
never on the live UDP feed, which has no camera. Both attributes are **absent when it was not
measured** (no depth capture paired with the frame, or the vehicle projects outside it); an absent
attribute means *unknown*, which is not the same claim as "nothing is in the way". Measurement and
tuning: [17_Photoreal_Occlusion_Metric.md](17_Photoreal_Occlusion_Metric.md).

### 5.2 Which vehicles are reported

Truth is emitted only for vehicles that have **arrived in the scene**. Boundary-aware staging traffic
spawns a vehicle transparent out in the entry ring and dissolves it in as it crosses into the
interior; one that has never been fully opaque has not arrived and is left out, because a
half-dissolved car is not something a sensor should be told is there. A vehicle that has arrived stays
reported while it dissolves back out on its way off the map. Vehicles nothing fades — a hero vehicle,
scenario traffic, anything spawned by hand — are reported from the moment they spawn, so this makes no
difference to a run without staging traffic.

## 6. Producers

- **Truth (this work):** a shim helper `world.get_vehicle_telemetry()` returns structured records (the §3/§5
  fields); a `to_cot(record)` formatter renders the §2 event; a reference emitter pushes them.
- **Detection (future):** reuses `to_cot()`; only the record producer differs (YOLO + pixel→world).

## 7. Transport (Option B reference emitter)

- **Pull:** `get_vehicle_telemetry()` is a plain client call — any consumer polls at its own rate (≥5 Hz)
  and formats/sends however it likes (honors the "pull and serve however you want" goal).
- **Reference emitter:** a small script streams CoT to **WinTAK** over **UDP** (WinTAK ingests CoT/UDP
  natively; TCP optional). This is a ~50-line swappable shim — not coupled to CARLA.
- **Not building:** an in-engine SignalR/WebSocket broker (Option C) — only if a fan-out consumer later
  demands it.

## 8. Decisions (resolved 2026-06-12)

1. **Default affiliation = neutral `n`** (civilian sim traffic). Per-vehicle override (from `role_name` /
   a scenario map) remains supported. ⇒ truth `type` = **`a-n-G-E-V`**.
2. **Symbol granularity = v0 single `a-n-G-E-V`** for every vehicle; fine class rides in `_carla`
   (`base_type`, `type_id`). Per-class SIDCs deferred to v1 (§4 table, pending an authoritative 2525 check).
3. Heading = **course-over-ground** from velocity (`atan2(vx, -vy)`), vehicle-yaw fallback below ~0.5 m/s;
   callsign = **`<base_type>-<id>`**; **`STALE_SECONDS = 3`**; truth **`ce = le = 0`**.

## 9. Verification (the payoff)

Truth and detection emit identical-shape CoT. A scoring harness associates tracks (position/time gate),
then reports: horizontal position error (truth.point vs detection.point), HAE error, classification
confusion (truth `base_type` vs detection class), and missed/false tracks — the core of the planned
truth-vs-CV comparison.
