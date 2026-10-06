# Light: what a scenario declares, and what it must never do with it

The sun is derived context. A scenario declares what its time means and a default for how the sun
behaves; the operator chooses the window and may override the policy; the pipeline records the sun it
got. Light is never a label.

Design record: `Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/07_Scenario_Authoring.md` §3.5.2,
§5.6 and §8.2.2; the bands and the night verdict are `11_Time_And_Illumination.md` §4.4 and D11.7.

## The illumination default

Required (check 39), read by the session's own `CarlaNet.CoSim.IlluminationPolicy`:

| `policy` | The sun across a window |
|---|---|
| `freeze_at_window_start` | set to the civil instant the window opens and held there. **Recommended**: one lighting condition per window |
| `advance` | carried forward at `rate_sun_s_per_sim_s`, sun-clock seconds per simulated second; the report names the arc from the open to the close of each window (check 39 warns) |
| `freeze_at` | held at `freeze_at_civil_time` |
| `ignore` | not bound: the world keeps whatever sun it has |

It is an authored default the operator may override at run start; both the default and the value used
reach the run manifest.

## Capture windows are candidates

`capture_windows[]` are windows the author offers, not run instructions. A window must lie inside the run
and may not cut a declared supervision interval (check 38): a partially observed positive teaches a
truncated pattern. The report gives each window's civil date and the sun it opens and closes under, and
warns when the window's civil date and the date its sun is written with differ (check 36).

## The bands

One definition, `11_Time_And_Illumination.md` §4.4, by the refraction-corrected sun elevation a
declaration is made against; the statistic below buckets by it and the vocabulary spells it:

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

**Below −6° the scene holds no light source at all** (doc 11, D11.7), and under SUMO drive nothing
switches a headlight on. The compiler states each window's lowest sun elevation and its band in the
resolution report (`capture_windows[].sun_lowest`, check 42) and concludes nothing — neither a warning
nor a refusal — so an author who captures a night window does it knowing the number, and a night window
still yields complete behavioural truth. At the sizing site 23:00 is −38° to −80° on every date.

## The illumination–label association

If behaviour correlates with the hour, the light correlates with the label, and a model trained on the
corpus can learn the clock. The compiler computes `I(band; supervision) / H(supervision)` over the
declared windows and over the span, records it in the lock and **warns, always, never refusing**
(check 41): in a pattern of life the correlation is structural — the sizing scenario measures 0.600, with
two hours wholly annotated. Report the number to the author in words, with the degenerate bands and
the remedies the resolution report lists.

## Sweeping the light

A sweep that varies behaviour holds illumination (check 43). To vary the light, **sweep `epoch.date`,
not the window hour**: the date moves the sun while the traffic the route files declare stays identical;
the hour moves both.
