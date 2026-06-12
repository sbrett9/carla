# Discussion — Telemetry Serving: Options, Standards, and the Truth-vs-Detection Pipeline

**Date:** 2026-06-12
**Context:** Before starting **06-telemetry** (the ≥5 Hz per-vehicle georeferenced "ADS-B-like" truth
deliverable), we paused to settle *how* telemetry should be served/emitted and in *what* format.
Datum is already locked to **ellipsoidal WGS84 (HAE)** (see `project_datum_decision`, PROJECT_STATE §0.5).
This is a thinking/options discussion — **no code written.**

Related: [../Findings/06_Elevation_Strategy.md](../Findings/06_Elevation_Strategy.md) §4.5 (telemetry wiring),
[../Findings/08_Layer_Architecture.md](../Findings/08_Layer_Architecture.md) (the `ground` sample source),
`CarlaNet/python/eo_observer.py` (the Ctrl+LMB depth pick — prototype of the detection→geo localizer).

---

## The prompt (user → assistant, verbatim)

> So, for the telemetry, I was originally thinking about serving it over websocket, allowing anyone to
> connect up and stream it. Something like signalR came to mind (though I am not sure how easily that
> would integrate into something like Carla).
> However, I also came to the conclusion I could also be thinking about this wrong.
> CarlaNet and the Python API allows pretty much anything that has the wheel file to sample the world at
> whichever rate they desire (or at least at 5 hz) and retrieve the telemetry of all vehicles being
> controlled by the Traffic Manager does it not?
> It may be better to instead simply allow those willing to use the CarlaNet Python SHIM to pull the
> information from the world itself as they need it and serve/send it however they desire.
>
> I think, before I make any further decisions, I need to know what sort of information about vehicles
> can be retrieved from a running CarlaServer via the CarlaNet and Python SHM.
>
> A Carla client using the python api could translate that vehicle information into any format it desired
> should it need to. What I do not know, is whether there is any sort of good standard for ground based
> vehicle telemetry that any client would want to translate the information pulled from the server into.
> I imagine there is some ADS-B-ish standard for ground based vehicles, perhaps a kind that even includes
> a vehicles's dimensions, purpose, classification, or even make/model license number, but I am not
> familiar with ADS-B standards at all.
> When it comes to COT, I have some experience with MIL-STD-2525B. Perhaps that would be a good choice?
>
> I think the main thing to consider here is that Carla houses the truth data. Any telemetry retrieved
> directly from it instead of via a YoloV8 computer vision model should be treated as truth data.
> Telemetry emitted by the analysis of the pixels retrieved from Carla via a YoloV8 model would be the
> telemetry we'd want verified against the Carla truth source. So, in that respect having the telemetries
> be similar in form would be best so the comparison is simple.
> I wager the Computer Vision model will emit bounding box info over the pixel and some other identifying
> sorts of data about the object identified, but for our purposes, something will have to convert that
> bounding box info into a pixel to world coordinate for the sake of the sorts of tactical tracking I
> plan on using Carla to build scenarios for.
> Eventually I do plan to have a YoloV8 computer vision model or some sort of detection model in here and
> that model will almost certainly have to emit cot style messages which get sent to something like WinTak.
> I also do not know enough yet about how to use the segmentation capability in Carla to create imagery
> with which to train the computer vision model (as I am unfamiliar with the labelling mechanism of the
> model we have). I would say for now assume something like Yolo-V8, though I do not know what it will
> really be, I wager something like Yolo-V8 is a good analog for the time being.
> Carla being built for training vehicle autopilot models requires the segmentation views to have
> different colors for different things, but for my situation, the roads and the vehicles are really all
> we have at this point. We have no means to segment a building from a tree or fields of grass due to the
> photogrammetry not really having that data (although perhaps we could bring in the OSM buildings tileset
> to some effect there, I am not sure how much data that tileset has for rural areas).
> So, this isn't so much refinement, but more my thoughts on the topic and what I really need are options
> for how best to proceed.

---

## 1. What you can pull per vehicle (all truth, all client-side)

Every vehicle the world observer caches exposes the following — and these are **cache reads, not
per-actor RPCs** (the observer stream updates them each tick), so polling *all* vehicles at 5 Hz (or much
faster) is cheap. Source: `CarlaNet/python/carlanet/__init__.py` `class Actor` (L672+) / `class Vehicle`.

| Field | Accessor | Gives you |
|---|---|---|
| `id` | `Actor.id` | stable track id |
| `type_id` | `Actor.type_id` | classification **incl. make/model** — e.g. `vehicle.audi.tt`, `vehicle.carlamotors.firetruck` |
| `bounding_box.extent` | `Actor.bounding_box` | **dimensions** (L×W×H half-extents) |
| `get_transform()` | observer cache | position (x,y,z) + **heading** (yaw) |
| `get_velocity()` | observer cache | 3D velocity → **ground speed + vertical rate** |
| `get_acceleration()`, `get_angular_velocity()` | cache | dynamics |
| `attributes` (dict) | blueprint | `color`, `role_name`, `number_of_wheels`, … |
| `get_control()`, `get_light_state()` | cache | throttle/brake/gear; turn-signal / brake-light state |

Convert `(x,y,z) → Geodesy.CarlaLocalToGeodetic → (lat, lon, HAE)` and you have a **richer-than-ADS-B**
truth track: position, velocity, heading, **size, classification, make/model, color**. No license plate
(CARLA has none), but everything else.

**Conclusion:** the user's reconsidered instinct is correct — anything with the wheel can pull complete
vehicle truth at any rate. A heavyweight in-engine server is **not** required to expose truth.

## 2. Standards landscape

- **ADS-B** — aviation (1090ES / DO-260): WGS84 position, velocity, callsign, ICAO 24-bit address,
  emitter *category*. **No dimensions, no make/model.** Not a civilian *ground-vehicle* standard.
  "ADS-B for cars" effectively does not exist.
- **SAE J2735 BSM** (the V2X "Basic Safety Message") — the real **ground-vehicle analog**: WGS84 position,
  speed, heading, acceleration set, **vehicle size + classification**. But it's a heavy ASN.1/UPER binary
  for V2X radios — **use it as a *field-model reference*, not the wire format.**
- **CoT (Cursor-on-Target)** — the TAK format. Its `type` string **is** a MIL-STD-2525 SIDC hierarchy
  (affiliation / battle-dimension / function), so the user's **2525B** experience maps directly. Native to
  **ATAK / WinTAK** over TCP/UDP/TLS. `<detail>` is extensible XML → embed J2735-style truth extras
  (dimensions, type, exact ground truth) in a custom subelement WinTAK ignores but a scoring harness reads.

**Recommendation: CoT on the wire; J2735-inspired fields inside `<detail>`.** It satisfies the WinTAK
target, leverages 2525B, and (below) gives truth/detection parity.

## 3. The unifying insight — truth and detection are the SAME pipeline

This is what makes "similar form for easy comparison" fall out for free:

- **Truth path:** vehicle actor → `Geodesy` → **CoT**. Exact.
- **Detection path (future YOLO):** RGB pixels → 2D bbox + class → take the bbox's ground pixel →
  **the exact depth-camera pixel→world reconstruction already built for `eo_observer` Ctrl+LMB** →
  `Geodesy` → **CoT**. Estimated / noisy.
- Both emit **CoT** → WinTAK and/or a scoring harness. Identical shape → trivial truth-vs-detection scoring
  (position error, class confusion, missed / false tracks).

So the **Ctrl+LMB "measure" tool is the prototype of the detection→geo localizer.** Designing truth as CoT
now means the future detector merely has to match a contract that already exists.

## 4. Segmentation / training data — the honest picture

- CARLA has `sensor.camera.semantic_segmentation` and `…instance_segmentation`. They label **CARLA-spawned
  actors** — your **vehicles** (and the OpenDRIVE road) get clean class / instance masks.
- **The Cesium photoreal is NOT a CARLA semantic actor**, so the seg camera labels it **Unlabeled**. Same
  for the **OSM Buildings tileset** (also a Cesium tileset, untagged) — so it **won't** help labelling, and
  rural OSM-building coverage is sparse anyway. The user's intuition holds: you cannot segment
  building / tree / grass out of photogrammetry.
- **But that's fine for a vehicle detector:** you get **clean vehicle masks against an unlabeled photoreal
  backdrop** — exactly the training signal needed. Cleanest YOLO-label generator: **project each vehicle's
  3D bounding box to 2D** (CARLA's documented technique; same intrinsics math as the depth pick) → 2D bbox
  + class per frame, no seg camera required (or instance-seg for YOLOv8-seg masks). **Separate workstream**
  from telemetry — noted, not conflated.

## 5. Options to proceed

Recommendation: **do NOT build a server inside CARLA** (no SignalR-in-CARLA). Build a small composable layer.

| Option | What it is | When |
|---|---|---|
| **A — Library only** | A shim helper `world.get_vehicle_telemetry()` returning structured truth records; consumers format/serve however they like. | Max flexibility, least code |
| **B — Library + CoT (recommended)** | A + a `to_cot(record)` formatter + a reference **CoT-over-UDP/TCP emitter to WinTAK**. | Working truth→TAK feed now; sets the CoT contract the detector later matches |
| **C — Full service** | A/B + a WebSocket / SignalR broker for many remote / browser subscribers. | Only once a consumer actually demands pull / fan-out |

**Recommended: B.** It honors "let clients pull and format how they want" (the helper is just a pull),
ships a real truth feed to WinTAK, and — most importantly — **defines the CoT contract once**, which both
the truth source and the future YOLO detector satisfy, making verification a direct diff.

## 6. Open decisions / next step

- **Pick an option** (A / B / C) — leaning **B**.
- **Draft a "telemetry contract" doc**: the per-vehicle field set + exact CoT mapping (2525 `type` per
  vehicle class, `<point>` lat/lon/hae/ce/le, the truth-extras `<detail>` block), so truth and detector are
  locked to one schema **before** any code.
- Still undecided downstream: noise/error model for the *detection* feed (truth stays exact); whether the
  reference emitter is UDP or TCP to WinTAK; CoT `how`/`stale` timing conventions.

**Status:** discussion only; 06-telemetry implementation not started. The user's stated next move was to
have this discussion captured first.
