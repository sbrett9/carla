# Resolution report: Gardnerville_Centerville_Lane_NeighborhoodOrbit

**Outcome:** compiled

**Produced by:** carlacontrol.ScenarioCompiler 0.10.0+g4e7614850.dirty, carlanet unknown, SUMO 1.27.0, at 2026-10-08T01:57:41.898Z

One marked vehicle, orbiter, enters on Centerville Lane at the posted limit, drives the Rock Terrace Drive block 20 times held to 11 m/s, and leaves west at 1.25 times the posted limit, among directional corridor traffic and neighborhood through traffic. Written by CarlaControl/scripts/make_sumo_scenario.py; edit that, not this.

## Findings

| Check | Outcome | Subject | Finding |
|---|---|---|---|
| 17 | warn | vehicle class suv | draws one body, vehicle.nissan.patrol, so every vehicle of the class looks the same and its appearance can become its label |
| 17 | warn | vehicle class van | draws one body, vehicle.sprinter.mercedes, so every vehicle of the class looks the same and its appearance can become its label |
| 17 | warn | vehicle class orbiter | draws one body, vehicle.lincoln.mkz, so every vehicle of the class looks the same and its appearance can become its label |
| 41 | warn | supervision | I(band; supervision) / H(supervision) = undefined (one supervision state only) over 31 entries (at their departures; no capture window is declared). Expected in a pattern of life and never a refusal; the table, the bands where both states occur and the ways of changing the numbers are in the resolution report, and the statistic is in the lock |

## Epoch

t = 0 is 2026-06-21T10:00:00-07:00 (2026-06-21T17:00:00Z), UTC-07:00 including daylight saving, calendar held at the epoch's date, America/Los_Angeles; the run ends at 2026-06-21T10:37:00-07:00.

`epoch_block_sha256` 3cb60fce65c132642954c1b12fc4d7276b18a2476cfe16d795073fdd79a94ac0. The zone name, when declared, is carried and never resolved.

## The sun's zone

Declared offset -7 h; the world's georeference configures -7.98431 h. set_solar_epoch writes the declared offset as the sun's zone.

## Illumination default

Policy `freeze_at_window_start`: an authored default the operator may override.

## Illumination-label association (check 41)

I(band; supervision) / H(supervision), base 2; bands by 11_Time_And_Illumination.md §4.4; elevation refraction_corrected. Presence: a vehicle is present from its departure for its free-flow route time plus its stops; a flow from its begin to its end.

### Over the declared windows

Not computed: no capture window is declared.

### Over the span, at each departure

Normalized mutual information: undefined over 31 entries.

| Band | annotated | nominal | unlabelled | total |
|---|---|---|---|---|
| day | 0 | 0 | 31 | 31 |

Bands where one state alone occurs: none. Bands where both occur: none.

Remedies:

- pair an annotated behavior with a displaced-in-time counterfactual, so the same annotation appears in a second band
- add a nominal twin inside the annotated band: a hard negative lit identically
- add a capture window in a band where the annotated class is absent, which turns a degenerate band into a populated one

## Places

| Place | Authored | Became | Street |
|---|---|---|---|
| 108141475#0 | `{"edge": "108141475#0"}` | 108141475#0 | Centerville Lane |
| 108141475#2 | `{"edge": "108141475#2"}` | 108141475#2 | Centerville Lane |
| 108141475#3 | `{"edge": "108141475#3"}` | 108141475#3 | Centerville Lane |
| 108141475#4 | `{"edge": "108141475#4"}` | 108141475#4 | Centerville Lane |
| -219060582#2 | `{"edge": "-219060582#2"}` | -219060582#2 | Cobblestone Drive |
| 219060581#4 | `{"edge": "219060581#4"}` | 219060581#4 | Rock Terrace Drive |
| -219060584#0 | `{"edge": "-219060584#0"}` | -219060584#0 | Keystone Court |
| 219060581#1 | `{"edge": "219060581#1"}` | 219060581#1 | Rock Terrace Drive |
| 219060581#2 | `{"edge": "219060581#2"}` | 219060581#2 | Rock Terrace Drive |
| 219060581#3 | `{"edge": "219060581#3"}` | 219060581#3 | Rock Terrace Drive |
| 219060582#2 | `{"edge": "219060582#2"}` | 219060582#2 | Cobblestone Drive |
| -108141475#4 | `{"edge": "-108141475#4"}` | -108141475#4 | Centerville Lane |
| -108141475#3 | `{"edge": "-108141475#3"}` | -108141475#3 | Centerville Lane |
| -108141475#2 | `{"edge": "-108141475#2"}` | -108141475#2 | Centerville Lane |
| -108141475#1 | `{"edge": "-108141475#1"}` | -108141475#1 | Centerville Lane |
| 1236933617#2 | `{"edge": "1236933617#2"}` | 1236933617#2 | Centerville Lane |
| -1236933617#2 | `{"edge": "-1236933617#2"}` | -1236933617#2 | Centerville Lane |
| 1419984783#1 | `{"edge": "1419984783#1"}` | 1419984783#1 | Pleasantview Drive |
| -1419984783#1 | `{"edge": "-1419984783#1"}` | -1419984783#1 | Pleasantview Drive |
| 14286827 | `{"edge": "14286827"}` | 14286827 | Edna Drive |
| -14286827 | `{"edge": "-14286827"}` | -14286827 | Edna Drive |
| 14288285 | `{"edge": "14288285"}` | 14288285 | Marianne Way |
| -14288285 | `{"edge": "-14288285"}` | -14288285 | Marianne Way |
| 14289737 | `{"edge": "14289737"}` | 14289737 | Rubio Way |
| -14289737 | `{"edge": "-14289737"}` | -14289737 | Rubio Way |
| -14286316 | `{"edge": "-14286316"}` | -14286316 | Heavenly View Court |
| 14286316 | `{"edge": "14286316"}` | 14286316 | Heavenly View Court |
| -1428171646 | `{"edge": "-1428171646"}` | -1428171646 |  |
| 1428171646 | `{"edge": "1428171646"}` | 1428171646 |  |
| 14285460 | `{"edge": "14285460"}` | 14285460 |  |
| -14285460 | `{"edge": "-14285460"}` | -14285460 |  |
| 219060581#0 | `{"edge": "219060581#0"}` | 219060581#0 | Rock Terrace Drive |
| -219060581#2 | `{"edge": "-219060581#2"}` | -219060581#2 | Rock Terrace Drive |
| -219060581#1 | `{"edge": "-219060581#1"}` | -219060581#1 | Rock Terrace Drive |
| -219060581#0 | `{"edge": "-219060581#0"}` | -219060581#0 | Rock Terrace Drive |
| 219060584#0 | `{"edge": "219060584#0"}` | 219060584#0 | Keystone Court |
| -219060581#3 | `{"edge": "-219060581#3"}` | -219060581#3 | Rock Terrace Drive |
| -219060582#1 | `{"edge": "-219060582#1"}` | -219060582#1 | Cobblestone Drive |
| -219060582#0 | `{"edge": "-219060582#0"}` | -219060582#0 | Cobblestone Drive |
| 219060582#0 | `{"edge": "219060582#0"}` | 219060582#0 | Cobblestone Drive |
| 219060583 | `{"edge": "219060583"}` | 219060583 | Lost River Lane |
| -219060583 | `{"edge": "-219060583"}` | -219060583 | Lost River Lane |
| -219060581#4 | `{"edge": "-219060581#4"}` | -219060581#4 | Rock Terrace Drive |
| -219060584#1 | `{"edge": "-219060584#1"}` | -219060584#1 | Keystone Court |
| 219060584#1 | `{"edge": "219060584#1"}` | 219060584#1 | Keystone Court |

## Routes

| Id | Type | Departs | Route | Length | Free-flow |
|---|---|---|---|---|---|
| orbiter | orbiter | 2026-06-21T10:01:00-07:00 | 108141475#0 108141475#2 108141475#3 108141475#4 -219060582#2 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060581#4 -219060584#0 219060581#1 219060581#2 219060581#3 219060582#2 -108141475#4 -108141475#3 -108141475#2 -108141475#1 | 19616.4 m | 1356.9 s |
| corridor_west_to_northeast | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 108141475#0 108141475#2 108141475#3 108141475#4 108141475#5 108141475#6 1236933617#0 1236933617#1 1236933617#2 | 1929.48 m | 95.9 s |
| corridor_northeast_to_west | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -1236933617#2 -1236933617#1 755567030 -108141475#5 -108141475#4 -108141475#3 -108141475#2 -108141475#1 | 1930.51 m | 95.8 s |
| corridor_west_to_east | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 108141475#0 108141475#2 108141475#3 108141475#4 108141475#5 108141475#6 1419984783#0 1419984783#1 | 1585.19 m | 78.4 s |
| corridor_east_to_west | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -1419984783#1 -1419984783#0 -108141475#6 -108141475#5 -108141475#4 -108141475#3 -108141475#2 -108141475#1 | 1585.12 m | 78.4 s |
| corridor_northeast_to_east | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -1236933617#2 -1236933617#1 -1236933617#0 1419984783#0 1419984783#1 | 525.16 m | 25.7 s |
| corridor_east_to_northeast | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -1419984783#1 -1419984783#0 1236933617#0 1236933617#1 1236933617#2 | 519.79 m | 25.4 s |
| edna_to_west | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 14286827 -108141475#1 | 980.1 m | 58.1 s |
| west_to_edna | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 108141475#0 -14286827 | 982.14 m | 58.2 s |
| marianne_to_east | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 14288285 108141475#4 108141475#5 108141475#6 1419984783#0 1419984783#1 | 936.81 m | 55.1 s |
| west_to_marianne | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 108141475#0 108141475#2 108141475#3 -14288285 | 1453.08 m | 81.2 s |
| rubio_to_west | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 14289737 -1419984783#0 -108141475#6 -108141475#5 -108141475#4 -108141475#3 -108141475#2 -108141475#1 | 1854.84 m | 100.2 s |
| east_to_rubio | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -1419984783#1 -14289737 | 444.83 m | 29.7 s |
| heavenlyview_to_west | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -14286316 -108141475#4 -108141475#3 -108141475#2 -108141475#1 | 1709.83 m | 95.4 s |
| west_to_heavenlyview | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 108141475#0 108141475#2 108141475#3 108141475#4 14286316 | 1715.35 m | 95.8 s |
| northstub_to_west | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -1428171646 -1236933617#1 755567030 -108141475#5 -108141475#4 -108141475#3 -108141475#2 -108141475#1 | 2056.66 m | 108.4 s |
| west_to_northstub | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 108141475#0 108141475#2 108141475#3 108141475#4 108141475#5 108141475#6 1236933617#0 1236933617#1 1428171646 | 2055.1 m | 108.3 s |
| turningcircle_to_east | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 14285460 108141475#3 108141475#4 108141475#5 108141475#6 1419984783#0 1419984783#1 | 953.11 m | 52.9 s |
| east_to_turningcircle | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -1419984783#1 -1419984783#0 -108141475#6 -108141475#5 -108141475#4 -108141475#3 -14285460 | 953.92 m | 53 s |
| neighbourhood_through_north | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 108141475#0 108141475#2 108141475#3 219060581#0 219060581#1 219060581#2 -219060583 219060582#1 219060582#2 108141475#5 108141475#6 1236933617#0 1236933617#1 1428171646 | 2870.09 m | 171.3 s |
| neighbourhood_through_west | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -1236933617#2 -1236933617#1 755567030 -108141475#5 -219060582#2 -219060582#1 219060583 -219060581#2 -219060581#1 -219060581#0 -108141475#3 -108141475#2 -108141475#1 | 2756.5 m | 159.6 s |
| neighbourhood_south_to_north | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 14288285 219060581#0 219060584#0 -219060581#4 219060581#4 -219060581#4 -219060581#3 -219060583 219060582#1 219060582#2 108141475#5 108141475#6 1236933617#0 1236933617#1 1428171646 | 2371.83 m | 158.9 s |
| neighbourhood_north_to_south | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -1428171646 -1236933617#1 755567030 -108141475#5 -219060582#2 -219060582#1 219060583 219060581#3 -219060582#1 -219060582#0 -219060581#1 -219060581#0 -14288285 | 2413.75 m | 161.8 s |
| cobblestone_to_east | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 219060582#0 219060582#1 219060582#2 108141475#5 108141475#6 1419984783#0 1419984783#1 | 648.06 m | 38.6 s |
| east_to_cobblestone | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -1419984783#1 -1419984783#0 -108141475#6 -108141475#5 -219060582#2 -219060582#1 -219060582#0 | 650.99 m | 38.8 s |
| lostriver_to_east | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 219060583 219060581#3 219060582#2 108141475#5 108141475#6 1419984783#0 1419984783#1 | 794.01 m | 49.1 s |
| west_to_lostriver | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 108141475#0 108141475#2 108141475#3 108141475#4 -219060582#2 -219060582#1 219060583 -219060583 | 1644.28 m | 90.7 s |
| rockterrace_local_north | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -219060581#4 219060582#2 108141475#5 108141475#6 1236933617#0 1236933617#1 1428171646 | 970.95 m | 58 s |
| rockterrace_local_in | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 108141475#0 108141475#2 108141475#3 219060581#0 219060581#1 219060581#2 | 1772.72 m | 104.2 s |
| keystone_to_west | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | -219060584#1 -219060581#4 219060582#2 -108141475#4 -108141475#3 -108141475#2 -108141475#1 | 1479.79 m | 78.8 s |
| west_to_keystone | ambient_mix | 2026-06-21T10:00:00-07:00 to 2026-06-21T10:37:00-07:00 | 108141475#0 108141475#2 108141475#3 108141475#4 -219060582#2 219060581#4 219060584#1 | 1476.84 m | 78.6 s |

## Vehicle types

Bodies from catalogue `carla-0.10.0-windows` (vehicles.catalogue.json, digest `6037e3bb2bde6f45de45e31925236593d16653989293fe414d9060a78bfbe90d`). The lights are whether each lights up on the body when the session drives it: headlights by the sun, brake lights and turn signals from SUMO's signals.

- car: 9 measured bodies, length 4.18-5.37 m, mean 4.82 m, share 0.45
- suv: 1 measured body, length 5.59-5.59 m, mean 5.59 m, share 0.18
- van: 1 measured body, length 5.92-5.92 m, mean 5.92 m, share 0.07
- truck: 2 measured bodies, length 7.92-8.00 m, mean 7.96 m, share 0.05
- orbiter: 1 measured body, length 4.89-4.89 m, mean 4.89 m, share 0

| Type | Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|---|
| car.vehicle.ue4.audi.tt | vehicle.ue4.audi.tt | 4.181 | 1.967 | 1.385 | unlit | unlit | unlit |
| car.vehicle.mini.cooper | vehicle.mini.cooper | 4.553 | 2.090 | 1.772 | unlit | unlit | unlit |
| car.vehicle.ue4.bmw.grantourer | vehicle.ue4.bmw.grantourer | 4.611 | 2.144 | 1.667 | unlit | unlit | unlit |
| car.vehicle.ue4.mercedes.ccc | vehicle.ue4.mercedes.ccc | 4.674 | 1.805 | 1.442 | unlit | unlit | unlit |
| car.vehicle.ue4.ford.mustang | vehicle.ue4.ford.mustang | 4.718 | 1.835 | 1.301 | unlit | unlit | unlit |
| car.vehicle.lincoln.mkz | vehicle.lincoln.mkz | 4.892 | 1.833 | 1.524 | unlit | unlit | unlit |
| car.vehicle.dodge.charger | vehicle.dodge.charger | 5.006 | 1.854 | 1.540 | unlit | unlit | unlit |
| car.vehicle.ue4.chevrolet.impala | vehicle.ue4.chevrolet.impala | 5.357 | 1.779 | 1.411 | unlit | unlit | unlit |
| car.vehicle.ue4.ford.crown | vehicle.ue4.ford.crown | 5.366 | 1.782 | 1.575 | unlit | unlit | unlit |
| suv.vehicle.nissan.patrol | vehicle.nissan.patrol | 5.591 | 2.147 | 2.059 | unlit | unlit | unlit |
| van.vehicle.sprinter.mercedes | vehicle.sprinter.mercedes | 5.915 | 1.982 | 2.726 | unlit | unlit | unlit |
| truck.vehicle.carlacola.actors | vehicle.carlacola.actors | 8.004 | 2.787 | 4.055 | unlit | unlit | unlit |
| truck.vehicle.carlamotors.european_hgv | vehicle.carlamotors.european_hgv | 7.924 | 2.787 | 3.783 | unlit | unlit | unlit |
| orbiter.vehicle.lincoln.mkz | vehicle.lincoln.mkz | 4.892 | 1.833 | 1.524 | unlit | unlit | unlit |

## Supervision


## Dry run (check 59)

SUMO 1.27.0 alone over 2220 s: 775 vehicles loaded, 775 inserted, 0 discarded after waiting max-depart-delay, 0 still waiting at the end; 0 collisions, 0 teleports, 0 emergency stops, 1 emergency braking. 0 of the 0 vehicles the plan names entered; of the others, 0 were discarded and 0 were still waiting at the end.

## Traffic

SUMO seed 42, step 0.05 s, end 2220 s. The processing options, each written into the configuration rather than left to SUMO's default:

| Option | Value |
|---|---|
| `time-to-teleport` | -1 |
| `max-depart-delay` | 900 |
| `collision.action` | warn |
| `lanechange.duration` | 3 |

## Lock

```json
{
  "routes": {
    "path": "Gardnerville_Centerville_Lane_NeighborhoodOrbit.rou.xml",
    "sha256": "3fdbc905b467184c0b2507efd1fe2fa2bc2fa403e633ecfc6363d57ebd5bf1a1"
  },
  "config": {
    "path": "Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg",
    "sha256": "92b5d3164960164610cb50e98f147d35c150801da749ce606d9419e3193023de"
  },
  "network": {
    "path": "Gardnerville_Centerville_Lane.net.xml",
    "sha256": "20a6877216f222f877ad337f8da13b7fac4430441cafbbd8784f2353890457ef"
  },
  "supervision": {
    "path": "Gardnerville_Centerville_Lane_NeighborhoodOrbit.supervision.json",
    "sha256": "2b705fbb748949979630d074543394c21c4996fe33eba55ac5cc4b0e65c9a83d"
  }
}
```
