# Resolution report: Arapahoe_I25_SupervisionCheck

**Outcome:** compiled

**Produced by:** carlacontrol.ScenarioCompiler 0.10.0+g4e7614850, carlanet unknown, SUMO 1.27.0, at 2026-10-08T01:57:36.141Z

Six minutes on South Yosemite Street just north of East Arapahoe Road: a car waits two minutes at the curb, a second car drives through, and a van stops at the same curb for twenty seconds, among the Arapahoe dwell's own traffic on Arapahoe Road and Yosemite, so every kind of supervision the plan carries for a vehicle can be checked live. Written by CarlaControl/scripts/make_supervision_check_scenario.py; edit that, not this.

## Findings

| Check | Outcome | Subject | Finding |
|---|---|---|---|
| 17 | warn | vehicle class suv | draws one body, vehicle.nissan.patrol, so every vehicle of the class looks the same and its appearance can become its label |
| 17 | warn | vehicle class van | draws one body, vehicle.sprinter.mercedes, so every vehicle of the class looks the same and its appearance can become its label |
| 17 | warn | vehicle class truck | draws one body, vehicle.carlacola.actors, so every vehicle of the class looks the same and its appearance can become its label |
| 17 | warn | vehicle class semi | draws one body, vehicle.carlamotors.european_hgv, so every vehicle of the class looks the same and its appearance can become its label |
| 41 | warn | supervision | I(band; supervision) / H(supervision) = 0.098 over 22 entries (captured by the declared windows); bands where one state alone occurs, so the band determines the label: day. Expected in a pattern of life and never a refusal; the table, the bands where both states occur and the ways of changing the numbers are in the resolution report, and the statistic is in the lock |

## Epoch

t = 0 is 2026-09-29T07:26:00-06:00 (2026-09-29T13:26:00Z), UTC-06:00 including daylight saving, calendar advances, America/Denver; the run ends at 2026-09-29T07:32:00-06:00.

`epoch_block_sha256` b92057175df8fcf7d3f5070d76aac03c301b672e891bd7b044b888efa8a24831. The zone name, when declared, is carried and never resolved.

## The sun's zone

Declared offset -6 h; the world's georeference configures -6.9923 h. set_solar_epoch writes the declared offset as the sun's zone.

## Illumination default

Policy `freeze_at_window_start`: an authored default the operator may override.

## Capture windows (authored candidates)

| Window | Opens | Closes | Civil date | Sun date | Elevation at open | Elevation at close |
|---|---|---|---|---|---|---|
| dwell_golden | 2026-09-29T07:27:00-06:00 | 2026-09-29T07:30:00-06:00 | 2026-09-29 | 2026-09-29 | 5.69 | 5.69 |
| stop_day | 2026-09-29T07:30:00-06:00 | 2026-09-29T07:32:00-06:00 | 2026-09-29 | 2026-09-29 | 6.26 | 6.26 |

## Illumination-label association (check 41)

I(band; supervision) / H(supervision), base 2; bands by 11_Time_And_Illumination.md §4.4; elevation refraction_corrected. Presence: a vehicle is present from its departure for its free-flow route time plus its stops; a flow from its begin to its end.

### Over the declared windows

Normalized mutual information: 0.098 over 22 entries.

| Band | annotated | nominal | unlabelled | total |
|---|---|---|---|---|
| day | 0 | 1 | 9 | 10 |
| golden | 2 | 1 | 9 | 12 |

Bands where one state alone occurs: day. Bands where both occur: golden.

### Over the span, at each departure

Normalized mutual information: 0.398 over 12 entries.

| Band | annotated | nominal | unlabelled | total |
|---|---|---|---|---|
| day | 0 | 1 | 0 | 1 |
| golden | 2 | 0 | 9 | 11 |

Bands where one state alone occurs: day. Bands where both occur: golden.

Remedies:

- pair an annotated behavior with a displaced-in-time counterfactual, so the same annotation appears in a second band
- add a nominal twin inside the annotated band: a hard negative lit identically
- add a capture window in a band where the annotated class is absent, which turns a degenerate band into a populated one

## Places

| Place | Authored | Became | Street |
|---|---|---|---|
| i25_north_in | `{"gateway": "south", "travel": "in", "street": "South Valley Highway"}` | 37722905 | South Valley Highway |
| i25_north_out | `{"gateway": "north", "travel": "out", "street": "South Valley Highway"}` | 106308386 | South Valley Highway |
| i25_south_in | `{"gateway": "north", "travel": "in", "street": "South Valley Highway"}` | 472478085 | South Valley Highway |
| i25_south_out | `{"gateway": "south", "travel": "out", "street": "South Valley Highway"}` | 908324823 | South Valley Highway |
| i25_north_short_of_arapahoe | `{"edge": "907700111"}` | 907700111 | South Valley Highway |
| i25_north_past_arapahoe | `{"edge": "1001791386"}` | 1001791386 | South Valley Highway |
| i25_north_at_loop_merge | `{"edge": "1342047649"}` | 1342047649 | South Valley Highway |
| arapahoe_east_in | `{"edge": "131933384"}` | 131933384 | East Arapahoe Road |
| arapahoe_east_out | `{"edge": "427819527"}` | 427819527 | East Arapahoe Road |
| arapahoe_west_in | `{"edge": "427819540#0"}` | 427819540#0 | East Arapahoe Road |
| arapahoe_west_out | `{"edge": "427819541#0"}` | 427819541#0 | East Arapahoe Road |
| arapahoe_westbound_west_of_i25 | `{"edge": "629675735"}` | 629675735 | East Arapahoe Road |
| i25_north_exit_to_arapahoe_east | `{"edge": "131933449#0"}` | 131933449#0 |  |
| xanthia_street_northbound | `{"edge": "-17003602#7"}` | -17003602#7 | South Xanthia Street |
| arapahoe_westbound_at_yosemite | `{"edge": "427819537#1"}` | 427819537#1 | East Arapahoe Road |
| yosemite_northbound_from_arapahoe | `{"edge": "1026993839#0"}` | 1026993839#0 | South Yosemite Street |
| underpass_westbound | `{"edge": "218965860#0"}` | 218965860#0 | South Yosemite Street |
| underpass_eastbound | `{"edge": "-223315781"}` | -223315781 | South Yosemite Street |
| underpass_dwell | `{"lat": 39.600357, "lon": -104.88649, "max_snap_m": 2.0, "vclass": "passenger"}` | 218965860#0_0 at 88.63 m | South Yosemite Street |
| yosemite_north_in | `{"edge": "427819547"}` | 427819547 | South Yosemite Street |
| yosemite_north_out | `{"edge": "-427819547"}` | -427819547 | South Yosemite Street |
| yosemite_south_in | `{"edge": "-629629570"}` | -629629570 | South Yosemite Street |
| yosemite_south_out | `{"edge": "629629570"}` | 629629570 | South Yosemite Street |
| clinton_in | `{"edge": "-629634784"}` | -629634784 | South Clinton Street |
| clinton_out | `{"edge": "629634784"}` | 629634784 | South Clinton Street |
| caley_in | `{"edge": "132833790"}` | 132833790 | East Caley Avenue |
| caley_out | `{"edge": "629653938"}` | 629653938 | East Caley Avenue |
| peakview_west_in | `{"edge": "16999218"}` | 16999218 | East Peakview Avenue |
| peakview_east_out | `{"edge": "292396861#1"}` | 292396861#1 | East Peakview Avenue |
| peakview_east_in | `{"edge": "633447921"}` | 633447921 | East Peakview Avenue |
| peakview_west_out | `{"edge": "46107902#0"}` | 46107902#0 | East Peakview Avenue |
| boston_court_in | `{"edge": "17000739"}` | 17000739 | South Boston Court |
| arbor_in | `{"edge": "16998684#0"}` | 16998684#0 | East Arbor Drive |
| willow_in | `{"edge": "550665536#0"}` | 550665536#0 | South Willow Drive |
| wabash_in | `{"edge": "427479206"}` | 427479206 | South Wabash Way |
| davies_avenue_in | `{"edge": "16993828#0"}` | 16993828#0 | East Davies Avenue |
| davies_avenue_out | `{"edge": "-16993828#0"}` | -16993828#0 | East Davies Avenue |
| costilla_place_in | `{"edge": "16996647"}` | 16996647 | East Costilla Place |
| costilla_place_out | `{"edge": "-16996647"}` | -16996647 | East Costilla Place |
| costilla_avenue_in | `{"edge": "16996898"}` | 16996898 | East Costilla Avenue |
| costilla_avenue_out | `{"edge": "-16996898"}` | -16996898 | East Costilla Avenue |
| davies_place_in | `{"edge": "17001552#0"}` | 17001552#0 | East Davies Place |
| davies_place_out | `{"edge": "-17001552#1"}` | -17001552#1 | East Davies Place |
| briarwood_place_in | `{"edge": "17003522"}` | 17003522 | East Briarwood Place |
| briarwood_place_out | `{"edge": "-17003522"}` | -17003522 | East Briarwood Place |
| briarwood_boulevard_in | `{"edge": "17007347#0"}` | 17007347#0 | East Briarwood Boulevard |
| briarwood_boulevard_out | `{"edge": "-17007347#0"}` | -17007347#0 | East Briarwood Boulevard |
| briarwood_avenue_in | `{"edge": "224876698"}` | 224876698 | East Briarwood Avenue |
| briarwood_avenue_out | `{"edge": "-224876698"}` | -224876698 | East Briarwood Avenue |
| easter_place_in | `{"edge": "17006662#0"}` | 17006662#0 | East Easter Place |
| easter_place_out | `{"edge": "-17006662#0"}` | -17006662#0 | East Easter Place |
| fremont_circle_in | `{"edge": "-16991914"}` | -16991914 | East Fremont Circle |
| fremont_circle_out | `{"edge": "16991914"}` | 16991914 | East Fremont Circle |
| xanthia_street_in | `{"edge": "-17003598#2"}` | -17003598#2 | South Xanthia Street |
| xanthia_way_in | `{"edge": "-17006541"}` | -17006541 | South Xanthia Way |
| alton_way_in | `{"edge": "-17003147#17"}` | -17003147#17 | South Alton Way |
| alton_way_out | `{"edge": "17003147#12"}` | 17003147#12 | South Alton Way |
| kerb_dwell | `{"lane": "427819553#0_0", "offset_m": 70.0}` | 427819553#0_0 at 70 m | South Yosemite Street |
| kerb_brief_stop | `{"lane": "427819553#0_0", "offset_m": 40.0}` | 427819553#0_0 at 40 m | South Yosemite Street |
| yosemite_kerb_stretch | `{"edge": "427819553#0"}` | 427819553#0 | South Yosemite Street |
| arapahoe_eastbound_at_yosemite | `{"edge": "427819539#0"}` | 427819539#0 | East Arapahoe Road |
| yosemite_north_1 | `{"edge": "427819555#0"}` | 427819555#0 | South Yosemite Street |
| yosemite_north_2 | `{"edge": "427819546"}` | 427819546 | South Yosemite Street |
| yosemite_north_3 | `{"edge": "427819548"}` | 427819548 | South Yosemite Street |
| yosemite_north_4 | `{"edge": "427819545#0"}` | 427819545#0 | South Yosemite Street |
| yosemite_north_5 | `{"edge": "427819558#0"}` | 427819558#0 | South Yosemite Street |
| yosemite_north_6 | `{"edge": "16999198#0"}` | 16999198#0 | South Yosemite Street |
| yosemite_north_7 | `{"edge": "-427819557"}` | -427819557 | South Yosemite Street |
| yosemite_north_8 | `{"edge": "-427819554#2"}` | -427819554#2 | South Yosemite Street |
| yosemite_north_9 | `{"edge": "-714754657#0"}` | -714754657#0 | South Yosemite Street |

## Routes

| Id | Type | Departs | Route | Length | Free-flow |
|---|---|---|---|---|---|
| dweller | car | 2026-09-29T07:26:20-06:00 | 427819540#0 427819539#0 1026993839#0 427819553#0 427819555#0 427819546 427819548 427819545#0 427819558#0 16999198#0 -427819557 -427819554#2 -714754657#0 -427819547 | 936.39 m | 54.8 s |
| transit | car | 2026-09-29T07:27:30-06:00 | 427819540#0 427819539#0 1026993839#0 427819553#0 427819555#0 427819546 427819548 427819545#0 427819558#0 16999198#0 -427819557 -427819554#2 -714754657#0 -427819547 | 936.39 m | 54.8 s |
| brief_stopper | van | 2026-09-29T07:29:20-06:00 | 427819540#0 427819539#0 1026993839#0 427819553#0 427819555#0 427819546 427819548 427819545#0 427819558#0 16999198#0 -427819557 -427819554#2 -714754657#0 -427819547 | 936.39 m | 54.8 s |
| arapahoe_east_to_west | arterial_mix | 2026-09-29T07:26:00-06:00 to 2026-09-29T07:32:00-06:00 | 131933384 427819534 427819530#0 132789653 694506252#0 1037832984 75609577 629675735 775963749 132789650 427819537#0 427819537#1 427819541#0 | 703.77 m | 38.9 s |
| arapahoe_west_to_east | arterial_mix | 2026-09-29T07:26:00-06:00 to 2026-09-29T07:32:00-06:00 | 427819540#0 427819539#0 1026993836#0 1059880022 10378085 626534263 223306870 1037832983#0 775965544 427884543 427819525 131933321#0 427819527 | 714.56 m | 40 s |
| yosemite_north_to_south | arterial_mix | 2026-09-29T07:26:00-06:00 to 2026-09-29T07:32:00-06:00 | 427819547 714754657#0 427819554#2 -45806432 427819544 427819552#0 -427819558#1 -427819545#1 -427819548 -427819546 -427819555#1 -427819553#3 629653852 1278080473#0 1025703940 427819559#0 427819543#0 629653843 -427819551#8 -427819551#0 629653811#0 16999684 629653795 629653803 629629558 629629560 629629562 629629564 629629566 629629568 629629570 | 1824.71 m | 115.5 s |
| yosemite_south_to_north | arterial_mix | 2026-09-29T07:26:00-06:00 to 2026-09-29T07:32:00-06:00 | -629629570 -629629568 -629629566 -629629564 -629629562 -629629560 -629629558 -629653803 -629653795 -16999684 -629653811#1 629653818 427819551#1 1025703939#0 1026993839#0 427819553#0 427819555#0 427819546 427819548 427819545#0 427819558#0 16999198#0 -427819557 -427819554#2 -714754657#0 -427819547 | 1856.91 m | 105.5 s |
| yosemite_north_to_arapahoe_east | arterial_mix | 2026-09-29T07:26:00-06:00 to 2026-09-29T07:32:00-06:00 | 427819547 714754657#0 45798671#1 629653894 629653907 218965865#0 223928717 629634742#0 629634747 629634762 629634763 629634764#0 45760891#1 629634767 629634776 629634775 629634777 629634778#0 427819527 | 1142.93 m | 45.7 s |
| arapahoe_east_to_yosemite_north | arterial_mix | 2026-09-29T07:26:00-06:00 to 2026-09-29T07:32:00-06:00 | 131933384 427819534 427819530#0 629634769#0 629634768#0 629634766 629634765#0 629634760#1 1074718490 629634759 16996152#0 629634736 629653921 629653901 629653887#0 1389992986 1389992987 45798675#0 -714754657#0 -427819547 | 1133.32 m | 44.1 s |
| clinton_to_arapahoe_west | arterial_mix | 2026-09-29T07:26:00-06:00 to 2026-09-29T07:32:00-06:00 | -629634784 -629634783#2 45760824#0 629634780 629634781#0 132789653 694506252#0 1037832984 75609577 629675735 775963749 132789650 427819537#0 427819537#1 427819541#0 | 860.32 m | 41.9 s |
| arapahoe_west_to_clinton | arterial_mix | 2026-09-29T07:26:00-06:00 to 2026-09-29T07:32:00-06:00 | 427819540#0 427819539#0 1026993836#0 1059880022 10378085 626534263 223306870 1037832983#0 775965544 427884543 427819525 131933321#0 903817668#0 1384299163 629634782 629634784 | 871.05 m | 42.6 s |
| wabash_to_arapahoe_west | arterial_mix | 2026-09-29T07:26:00-06:00 to 2026-09-29T07:32:00-06:00 | 427479206 550665532 737508780#0 1035917283#0 223289022#0 427819554#2 -45806432 427819544 427819552#0 -427819558#1 -427819545#1 -427819548 -427819546 -427819555#1 -427819553#3 629653852 1278080473#0 1025703940 427819559#0 427819541#0 | 1186.69 m | 81.7 s |

## Vehicle types

Bodies from catalogue `carla-0.10.0-windows` (vehicles.catalogue.json, digest `6037e3bb2bde6f45de45e31925236593d16653989293fe414d9060a78bfbe90d`). The lights are whether each lights up on the body when the session drives it: headlights by the sun, brake lights and turn signals from SUMO's signals.

- car_quick: 9 measured bodies, length 4.18-5.37 m, mean 4.82 m, share 0
- car: 9 measured bodies, length 4.18-5.37 m, mean 4.82 m, share 0
- suv: 1 measured body, length 5.59-5.59 m, mean 5.59 m, share 0
- van: 1 measured body, length 5.92-5.92 m, mean 5.92 m, share 0
- truck: 1 measured body, length 8.00-8.00 m, mean 8.00 m, share 0
- semi: 1 measured body, length 7.92-7.92 m, mean 7.92 m, share 0

| Type | Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|---|
| car_quick.vehicle.ue4.audi.tt | vehicle.ue4.audi.tt | 4.181 | 1.967 | 1.385 | unlit | unlit | unlit |
| car_quick.vehicle.mini.cooper | vehicle.mini.cooper | 4.553 | 2.090 | 1.772 | unlit | unlit | unlit |
| car_quick.vehicle.ue4.bmw.grantourer | vehicle.ue4.bmw.grantourer | 4.611 | 2.144 | 1.667 | unlit | unlit | unlit |
| car_quick.vehicle.ue4.mercedes.ccc | vehicle.ue4.mercedes.ccc | 4.674 | 1.805 | 1.442 | unlit | unlit | unlit |
| car_quick.vehicle.ue4.ford.mustang | vehicle.ue4.ford.mustang | 4.718 | 1.835 | 1.301 | unlit | unlit | unlit |
| car_quick.vehicle.lincoln.mkz | vehicle.lincoln.mkz | 4.892 | 1.833 | 1.524 | unlit | unlit | unlit |
| car_quick.vehicle.dodge.charger | vehicle.dodge.charger | 5.006 | 1.854 | 1.540 | unlit | unlit | unlit |
| car_quick.vehicle.ue4.chevrolet.impala | vehicle.ue4.chevrolet.impala | 5.357 | 1.779 | 1.411 | unlit | unlit | unlit |
| car_quick.vehicle.ue4.ford.crown | vehicle.ue4.ford.crown | 5.366 | 1.782 | 1.575 | unlit | unlit | unlit |
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
| semi.vehicle.carlamotors.european_hgv | vehicle.carlamotors.european_hgv | 7.924 | 2.787 | 3.783 | unlit | unlit | unlit |

## Supervision

- **Arapahoe_I25_SupervisionCheck/kerbside_dwell**: annotated, labels check:kerbside_dwell; dweller (subject); parameters dwell_s = 120
  - dwell: from stop:0 to stop_end:0, 120 s declared
- **Arapahoe_I25_SupervisionCheck/through_transit**: annotated, labels check:through_transit; transit (subject)
  - transit: from depart (2026-09-29T07:27:30-06:00) to open
  - past_the_kerb: from phase:1 to phase:2
- **Arapahoe_I25_SupervisionCheck/brief_stop**: nominal, labels check:brief_kerb_stop; brief_stopper (subject); a hard negative for check:kerbside_dwell
  - stop: from stop:0 to stop_end:0, 20 s declared

## Dry run (check 59)

SUMO 1.27.0 alone over 360 s: 133 vehicles loaded, 133 inserted, 0 discarded after waiting max-depart-delay, 0 still waiting at the end; 0 collisions, 0 teleports, 0 emergency stops, 0 emergency braking. 3 of the 3 vehicles the plan names entered; of the others, 0 were discarded and 0 were still waiting at the end.

| Planned vehicle | Named by | Declared departure | Entered | Waited (s) |
|---|---|---|---|---|
| dweller | Arapahoe_I25_SupervisionCheck/kerbside_dwell | 2026-09-29T07:26:20-06:00 | 20.0 | 0 |
| transit | Arapahoe_I25_SupervisionCheck/through_transit | 2026-09-29T07:27:30-06:00 | 90.0 | 0 |
| brief_stopper | Arapahoe_I25_SupervisionCheck/brief_stop | 2026-09-29T07:29:20-06:00 | 200.0 | 0 |

## Traffic

SUMO seed 42, step 0.05 s, end 360 s. The processing options, each written into the configuration rather than left to SUMO's default:

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
    "path": "Arapahoe_I25_SupervisionCheck.rou.xml",
    "sha256": "016b810a19d4429fec8c65462b1937852e7b94379491d82ea603f1e839b21675"
  },
  "config": {
    "path": "Arapahoe_I25_SupervisionCheck.sumocfg",
    "sha256": "79a64f258e881cdb5d81638b6c4a95b467d036551ccdefc74f823bd9b1a856ff"
  },
  "network": {
    "path": "Arapahoe_I25.net.xml",
    "sha256": "78185aa8cd8bf2e4667ee64169a7edcd4ef115a0a14867c2c8547f89b1c509a6"
  },
  "supervision": {
    "path": "Arapahoe_I25_SupervisionCheck.supervision.json",
    "sha256": "c866ffeb735103f2720f98a4f838af8a4415434406b009d49fb3599fe728cc99"
  }
}
```
