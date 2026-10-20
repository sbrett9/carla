# Light: what a scenario declares, and what it must never do with it

The sun is derived context. A scenario declares what its time means and a default for how the sun
behaves; the operator chooses the window and may override the policy; the pipeline records the sun it
got. Light is never a label.

## The illumination default

Optional.\
The session's own `CarlaNet.CoSim.IlluminationPolicy` reads it and refuses a malformed one (check 39).\
Without one, or without a `policy` in it, the sun advances at a rate of 1.0.\
The report then names that policy as the default.

| `policy` | The sun across a window |
|---|---|
| `advance` | carried forward at `rate_sun_s_per_sim_s`, sun-clock seconds per simulated second. At a rate of 1.0 the sun follows simulated time. Without a rate, it moves at 1.0. The report names that rate as the default. The default policy |
| `freeze_at_window_start` | set to the civil instant the window opens and held there: one lighting condition per window |
| `freeze_at` | held at `freeze_at_civil_time` |
| `ignore` | not bound: the world keeps whatever sun it has |

The report states in one plain sentence what the sun does in each window: it climbs, sinks or holds.\
For the minimal example's window, it reads: "The sun climbs from -1.95° to +1.18° during this window."\
That is a fact, never a finding.

It is an authored default the operator can override at run start.\
Both the default and the value used reach the run manifest.

## Capture windows are candidates

`capture_windows[]` are windows the author offers, not run instructions. A window must lie inside the run
and may not cut a declared supervision interval (check 38): a partially observed positive teaches a
truncated pattern. The report gives each window's civil date and the sun it opens and closes under, and
warns when the window's civil date and the date its sun is written with differ (check 36).

## The bands

One definition, by the refraction-corrected sun elevation a declaration is made against; the
statistic below buckets by it and the vocabulary spells it:

| Band | Sun elevation |
|---|---|
| `day` | above +6° |
| `golden` | +6° to 0° |
| `civil_twilight` | 0° to −6° |
| `nautical_twilight` | −6° to −12° |
| `astronomical_twilight` | −12° to −18° |
| `night` | below −18° |

Each band holds its upper edge. They are terms the pipeline derives, never an author's labels.

## Night

**Below −6° the sun no longer lights the scene**, and the world has no moon and no street lamps. The
vehicles carry their own light: while SUMO drives, the session switches every rendered vehicle's
headlights on once the sun falls below +3° and off again once it rises above +6°, so every vehicle in
a night window has its headlights on. `carla-drive --no-vehicle-lamps` turns the lamps off for a
control run.

The compiler states each window's lowest sun elevation and its band in the resolution report
(`capture_windows[].sun_lowest`, check 42) and concludes nothing — neither a warning nor a refusal — so
an author who captures a night window does it knowing the number, and a night window still yields
complete behavioural truth. At Shahid Bahonar Port, 23:00 is −38° to −80° on every date.

## The illumination–label association

If behaviour correlates with the hour, the light correlates with the label, and a model trained on the
corpus can learn the clock. The compiler computes `I(band; supervision) / H(supervision)` over the
declared windows and over the span, records it in the lock and **warns, always, never refusing**
(check 41): in a pattern of life the correlation is structural — the Shahid Bahonar Port scenario measures 0.600, with
two hours wholly annotated. Report the number to the author in words, with the degenerate bands and
the remedies the resolution report lists.

## Sweeping the light

A sweep that varies behaviour holds illumination (check 43). To vary the light, **sweep `epoch.date`,
not the window hour**: the date moves the sun while the traffic the route files declare stays identical;
the hour moves both.
