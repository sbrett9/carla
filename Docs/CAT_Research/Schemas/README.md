# Schemas of the files these tools write and read

Each page in this folder describes one kind of file: what writes it, what reads it, every field and its format version.\
Most kinds also have a schema in `CarlaControl/schemas/`, which carlacontrol installs.

Each schema names itself with a URN, `urn:carla-sumo-capture:schema:<kind>:<format version>`.\
The URN is a name, not an address, so nothing has to serve it.

The schemas are generated from the code that writes or reads each file, so they say what the writers write.\
A few are written by hand and say so below.

`carla-validate <path>` checks a capture folder, a world package, a vehicle catalog folder, or any folder holding the other files below, against the schemas.\
`carla-validate --write-schemas CarlaControl/schemas` writes every generated schema again after a writer or a reader changes.

The rule for format versions is the same for every file.\
A file that declares no version is version 1.\
A reader refuses a version newer than it knows rather than reading it in part.

## What a capture writes

A capture folder holds a PNG and a truth sidecar for every still.\
Under `truth/` it holds the run manifest and the world truth track.

| Page | Schema | URN | What it describes |
|---|---|---|---|
| [Truth sidecar](Truth_Sidecar.md) | `truth_sidecar.xsd` | `urn:carla-sumo-capture:schema:truth-sidecar:1` | The ground truth of a still's frame: the camera, the sun and every vehicle with its position, motion, size and box in pixels. |
| [PNG chunk `carla:capture`](PNG_Chunk_Capture.md) | `png_chunk_capture.schema.json` | `urn:carla-sumo-capture:schema:png-chunk-capture:1` | Which capture a still is: the frame and time it was rendered at, its run and what wrote it. |
| [PNG chunk `carla:solar`](PNG_Chunk_Solar.md) | `png_chunk_solar.schema.json` | `urn:carla-sumo-capture:schema:png-chunk-solar:1` | The sun the world reported on the still's tick. |
| [PNG chunk `carla:illumination`](PNG_Chunk_Illumination.md) | `png_chunk_illumination.schema.json` | `urn:carla-sumo-capture:schema:png-chunk-illumination:1` | What the still's sun was declared to be and how far the world's sun was from it. |
| [PNG chunk `carla:sensor`](PNG_Chunk_Sensor.md) | `png_chunk_sensor.schema.json` | `urn:carla-sumo-capture:schema:png-chunk-sensor:1` | Where the camera was and how it saw: its pose and its intrinsics. |
| [Run manifest](Run_Manifest.md) | `run_manifest.schema.json` | `urn:carla-sumo-capture:schema:run-manifest:1` | `truth/manifest.jsonl`: the record of a SUMO-driven run as it happened, one row per line. |
| [World truth track](World_Truth_Track.md) | `world_truth_track.tableschema.json` | `urn:carla-sumo-capture:schema:world-truth-track:2` | `truth/world_truth_track.csv`: every vehicle SUMO had at each sampled frame of the capture window, drawn or not. |
| [World truth track summary](World_Truth_Track_Summary.md) | `world_truth_track_summary.schema.json` | `urn:carla-sumo-capture:schema:world-truth-track-summary:2` | The summary beside the track: its format, what made it, its columns, what it holds and why the run ended. |

## Run records

What `carla-capture` writes about a run beside the run's capture.

| Page | Schema | URN | What it describes |
|---|---|---|---|
| [Run result](Run_Result.md) | `run_result.schema.json` | `urn:carla-sumo-capture:schema:run-result:1` | `run.result.json`: how a run ended, where everything it wrote is and what it observed. |
| [Run resolution report](Run_Resolution_Report.md) | `run_resolution.schema.json` | `urn:carla-sumo-capture:schema:run-resolution:1` | `run.resolution.json`: what a launch resolved and what its checks found, written whether it was accepted or refused. |
| [Run lock](Run_Lock.md) | `run_lock.schema.json` | `urn:carla-sumo-capture:schema:run-lock:1` | `run.lock.json`: what an accepted run is bound to: its scenario, world, catalog, epoch and effective configuration. |
| [Launch echo](Launch_Echo.md) | `launch_echo.schema.json` | `urn:carla-sumo-capture:schema:launch-echo:1` | The `launch_echo` object in the result and the resolution report: what a run says it will do before it starts. |

## Inputs a user writes

| Page | Schema | URN | What it describes |
|---|---|---|---|
| [Run configuration](Run_Configuration.md) | `run_configuration.schema.json` | `urn:carla-sumo-capture:schema:run-configuration:1` | `<name>.run.json` and the run's own `run.effective.json`: the settings of a capture run. |
| [Site profile](Site_Profile.md) | `site_profile.schema.json` | `urn:carla-sumo-capture:schema:site-profile:1` | The facts about one machine a run needs: the server's address, the SUMO installation and where files are kept. |
| [Scenario specification](Scenario_Specification.md) | `scenario.schema.json`, in the authoring skill | `urn:carla-sumo-capture:schema:scenario:1` | `<scenario>.scenario.json`: one scenario as its author writes it. |
| [Sweep](Sweep.md) | `sweep.schema.json`, in the authoring skill | `urn:carla-sumo-capture:schema:sweep:1` | `<sweep>.sweep.json`: one base scenario, the axes varied over it and counterfactual pairs. |
| [Epoch](Epoch.md) | `epoch.schema.json` | `urn:carla-sumo-capture:schema:epoch:1` | What simulated second zero is in civil time, in a specification or a file of its own. |
| [Display convention](Display_Convention.md) | `display_convention.schema.json` | `urn:carla-sumo-capture:schema:display-convention:1` | `<scenario>.display.json`: the CoT affiliation each vehicle population is shown with. |
| [Areas of interest](Areas_Of_Interest.md) | `area_of_interest.schema.json` | `urn:carla-sumo-capture:schema:area-of-interest:1` | `<extract>.aoi.geojson`: the named places a scenario can put behavior in, in latitude and longitude. |

## What the compiler writes

What `carla-compile-scenario` writes for a scenario or a sweep.

| Page | Schema | URN | What it describes |
|---|---|---|---|
| [Scenario lock](Scenario_Lock.md) | `scenario_lock.schema.json` | `urn:carla-sumo-capture:schema:scenario-lock:1` | `<scenario_id>.lock.json`: what a compiled scenario is bound to, by the SHA-256 of every file that decides it. |
| [Scenario resolution report](Scenario_Resolution_Report.md) | `scenario_resolution.schema.json` | `urn:carla-sumo-capture:schema:scenario-resolution:1` | `<scenario_id>.resolution.json`: what a compile resolved, every place, time, schedule and route, for its author to check. |
| [Supervision plan](Supervision_Plan.md) | `supervision_plan.schema.json` | `urn:carla-sumo-capture:schema:supervision-plan:1` | `<scenario_id>.supervision.json`: the scenario's labels, fixed when it is compiled. The file is the only place they travel. |
| [Sweep index](Sweep_Index.md) | `sweep_index.schema.json` | `urn:carla-sumo-capture:schema:sweep-index:1` | `<sweep_id>.sweep-index.json`: every member of a compiled sweep, its axis values and every finding. |
| [Scenario compiler checks](Scenario_Checks.md) | `scenario_checks.schema.json` | `urn:carla-sumo-capture:schema:scenario-checks:1` | `checks.json` beside the authoring skill: every check the compiler runs, by its stable id. |
| [SUMO files](SUMO_Files.md) | none from these tools: SUMO's own XSDs | none | The route, additional, configuration, network and type files, in SUMO's own formats. |

## World and level packages

| Page | Schema | URN | What it describes |
|---|---|---|---|
| [World package](World_Package.md) | none of its own | none | `<MapName>.cwp` and the entries it holds: one generated world in one file. |
| [World package manifest](World_Package_Manifest.md) | `world_package_manifest.schema.json` | `urn:carla-sumo-capture:schema:world-package-manifest:1` | `world.json` in a `.cwp`: where the world sits on the Earth and how it was built. |
| [Place index](Place_Index.md) | `place_index.schema.json` | `urn:carla-sumo-capture:schema:place-index:1` | `places.json` in a `.cwp`: which edges carry which street name and which way each heads. |
| [Solar frame](Solar_Frame.md) | `solar_frame.schema.json` | `urn:carla-sumo-capture:schema:solar-frame:1` | `solar.json` in a `.cwp`: the facts about the world's sun a start time is checked against. |
| [Resolved areas of interest](Areas_Resolved.md) | `areas_resolved.schema.json` | `urn:carla-sumo-capture:schema:areas-resolved:1` | `areas.resolved.json` in a `.cwp`: the areas of interest placed in CARLA meters and on SUMO lanes. |
| [Bare-earth grid](Bare_Earth_Grid.md) | none: a binary layout, described on its page | none | `bareearth.bin` in a `.cwp`: the drape offset and the bare-earth height at each grid point. |
| [Level package manifest](Level_Package_Manifest.md) | `level_package_manifest.schema.json` | `urn:carla-sumo-capture:schema:level-package-manifest:1` | `world.json` in a `PackageWorld` zip: what a delivered world is and what it needs. |
| [World interface version](World_Interface_Version.md) | none: a version in `DefaultWorldInterface.ini` | none | Which pairings of a delivered world and a CARLA package work. How an installer decides. |

## The vehicle catalog

| Page | Schema | URN | What it describes |
|---|---|---|---|
| [Vehicle catalog](Vehicle_Catalogue.md) | `vehicle_catalogue.schema.json` | `urn:carla-sumo-capture:schema:vehicle-catalogue:1` | `vehicles.catalogue.json`: the measurements of every vehicle body CARLA can draw and the classes a scenario asks for. |
| [Vehicle body widths](Vehicle_Body_Widths.md) | `vehicle_body_widths.schema.json` | `urn:carla-sumo-capture:schema:vehicle-body-widths:1` | `vehicle_body_widths.json`: each body's width without its mirrors, measured from the mesh. |
| [Vehicle types](Vehicle_Types.md) | `vehicle_types.xsd`, written by hand | `urn:carla-sumo-capture:schema:vehicle-types:1` | `vehicles.vtypes.rou.xml`: the catalog in SUMO's form and the parameters these tools set on a vehicle type. |

## Telemetry

What the tools send and write as Cursor-on-Target (CoT) telemetry.\
This section also covers the legacy files a SUMO bridge run reads or writes beside it.

| Page | Schema | URN | What it describes |
|---|---|---|---|
| [CoT telemetry stream](CoT_Telemetry_Stream.md) | `cot_telemetry.xsd`, written by hand, with `cot_event_body.xsd` | `urn:carla-sumo-capture:schema:cot-telemetry:1` | One UDP datagram of the live feed: one CoT event for one vehicle at one instant. `cot_event_body.xsd` holds the event's parts. It has no URN of its own. |
| [SUMO bridge event file](SUMO_CoT_Event_File.md) | `sumo_cot_events.xsd`, written by hand | `urn:carla-sumo-capture:schema:sumo-cot-events:1` | The file `carla-cot-telemetry --xml` writes: every event of a run, each with its whole record. |
| [SUMO bridge table](SUMO_CoT_Table.md) | `sumo_cot_telemetry.tableschema.json` | `urn:carla-sumo-capture:schema:sumo-cot-telemetry:1` | The file `carla-cot-telemetry --csv` writes: one row per vehicle per update. |
| [SUMO bridge table](SUMO_CoT_Table.md), its summary | `sumo_cot_telemetry_summary.schema.json` | `urn:carla-sumo-capture:schema:sumo-cot-telemetry-summary:1` | `<name>.summary.json` beside the table: its format, its columns and what made it. |
| [Legacy supervision gaps](Supervision_Gaps.md) | `supervision_gaps.schema.json` | `urn:carla-sumo-capture:schema:supervision-gaps:1` | `<name>.supervision.json` beside a bridge run: the described gaps of a legacy scenario, placed on the run's clock. |
| [Legacy scenario labels](Legacy_Labels.md) | `legacy_labels.schema.json` | `urn:carla-sumo-capture:schema:legacy-labels:1` | `*.labels.json`: what a scenario generator wrote before scenarios were compiled. Nothing writes it anymore. |
