# Places and instants: what the compiler resolves, and what it refuses

A route never carries a bare edge id and a departure never carries a multiplied hour. A specification
names a place or writes a civil time; the compiler resolves each, **refuses rather than guesses**, and
states in the resolution report what everything became. Read the report back.

Design record: `Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/07_Scenario_Authoring.md` §4.
Resolvers: `CarlaControl/src/carlacontrol/PlaceResolver.py::PlaceResolver` and
`CarlaControl/src/carlacontrol/CivilTimeResolver.py::CivilTimeResolver`.

## Place forms

What a place is used for decides what it must resolve to: an origin, a destination or a via needs one
edge; a stop needs one lane and a position. Every refusal below is check 7 unless it says otherwise.

| Form | Example | Resolves to |
|---|---|---|
| Edge | `{"edge": "218965860#0"}`, optionally `"offset_m": 87.93` | itself; with an offset, that position on its rightmost lane (past the lane's end: check 9) |
| Lane | `{"lane": "26413459_0", "offset_m": 58.9}` | that position (past the lane's end: check 9) |
| Area | `{"area": "tower_03"}` | the lanes inside or crossing the world's area; a stop, when the area holds exactly one lane |
| Street | `{"street": "East Arapahoe Road", "direction": "west"}` | every edge of that name heading that way, from the world's place index |
| Street at a cross street | adds `"at": "South Yosemite Street"` | the one edge of the run arriving at a junction the cross street meets |
| Street near a point | adds `"near": {"lat": …, "lon": …}` | the one edge of the run nearest the point |
| Geographic point | `{"lat": …, "lon": …, "max_snap_m": 25}`, optionally `"vclass": "army"` | the position on the nearest lane admitting the class — any road vehicle without one — with the snap distance in the report |
| Gateway | `{"gateway": "south", "travel": "in"}`, optionally `"street": …` | the edges entering (`in`) or leaving (`out`) the world on that side |
| Junction movement | `{"from_street": …, "to_street": …}`, optionally `from_direction`, `to_direction` | the two edges one connection joins, never the internal edge; a via, where it contributes both |

**What each refuses.**

- A name, edge, lane or area that is not in the world, listing the nearest names.
- A street, a gateway or a movement that is several where one is needed, listing every candidate with
  its direction, length and extent. `South Yosemite Street` is 65 edges on Arapahoe: narrow a street
  with `direction` and `at` or `near`, a gateway with `street`, a turn with `from_direction` and
  `to_direction`.
- A point past `max_snap_m`, naming the distance; a point equidistant (within 0.01 m) from lanes of two
  edges, naming both — move it towards one, or name the lane.
- A stop at a place naming no position, naming the forms that give one.
- A place whose class may not drive it: check 10, on the declared and on the routed edges.

**How a point is placed.** By the world's origin through the WGS84 transform the telemetry and the
imagery use, which agrees with the network's own projection within 0.4 mm on every shipped world; the
lane position is SUMO's, the projected arc length scaled by the lane's length over its shape length.

**What a gateway is.** An end served by a single road — its edges one id either way — within 10 m of the
network's boundary, on the side it lies nearest. Not netconvert's `dead_end` junction: a two-way road
cut by the clip ends at a `priority` junction holding its turnaround. On Arapahoe the four gateways of
South Valley Highway resolve to the four I-25 edges the dwell's author found by hand.

**On a map with few names** — Bahonar names 4.5 % of its edges — use points, gateways, areas and lanes.

## Instant forms

Resolved against the epoch; the report gives every instant's second and its civil time.

| Form | Example | Resolves to |
|---|---|---|
| Seconds | `25200` | itself |
| Day and clock | `"d0 07:00"`, `"d6 02:30"`, `"d3 23:00:00"`, `"d0 24:00"` | the clock on the epoch's date plus N days, at the epoch's offset, less the epoch |
| Clock alone | `"07:00"` | day 0; only on a run of one day or less (check 47) |
| Absolute instant | `"2026-03-21T07:00:00+03:30"` | the instant less the epoch; at the epoch's own offset only (check 47) |
| Duration | `"8h"`, `"30m"`, `"274s"`, `"1h30m"`, `"7d"` | seconds |
| Named instant | `{"instant": "night_shift_start"}` | what `instants{}` declared (an unknown name: check 8) |
| Offset | `{"instant": "night_shift_start", "plus": "15m"}`, `{"at": "d2 11:00", "plus": 274}` | that second plus the duration |

A departure, a stop's `until` or an interval's begin outside `[0, end]` is refused (check 37); a flow or
an interval running past the end warns (checks 32, 31). A rota that expands to nothing, or a skip that
matches no occasion or gives no reason, is refused (check 48).
