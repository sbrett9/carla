# Matching your tracks to our truth

This page is for teams that receive capture folders from SUMO-driven captures and want to carry our
labels onto their own detector's tracks.

**We label; we never match and never score.** Matching a track to a truth vehicle needs your
detector's output, and no part of this system ever sees it. So we do two things only: we write truth
in a form that can be matched by position and time, and we publish the rule below so every team
matches the same way. Nothing in a capture folder depends on a model having run.

## What you match against

A capture folder holds one folder per camera, with a PNG and a truth sidecar (`.xml`) of the same name
for every still, and a `truth/` folder with the run manifest (`manifest.jsonl`) and the world truth
track. Each sidecar describes its own still's frame (its `tick` and `sim_time_s`), and a still is never
written with truth from another frame.

For every vehicle in the picture, the sidecar's `_carla` element carries:

- `box_px`, the upright box in this still's pixels (left, top, right, bottom), and `box_oriented_px`, the
  box turned with the vehicle (four corners). Both are already projected through this still's own camera
  pose and intrinsics, which the sidecar also records.
- `apparent_width_px` and `apparent_height_px`, `in_frame`, `truncation`, `occlusion` and
  `camera_range_m`.
- the eight corners of the 3D box in `<_box3d frame="geodetic">`.
- the author's labels for that frame, in `<_supervision>`.

## The rule, per camera and per still

1. **Match by position and time, never by id.** Our ids (`CARLA-TRUTH-SUMO-<sumo_id>`, `actor_id`,
   `sumo_id`) are for joining truth to truth. Your tracks carry none of them. If one of our ids ever
   turns up in your detector's output, it leaked from what we handed over: please tell us.
2. **Compare in the image, not on the ground.** Take the distance in pixels between your box's center
   and our `box_px` center, divided by our vehicle's apparent size. A distance on the ground mixes up
   two different mistakes: matching the wrong vehicle, and matching the right vehicle but locating it
   poorly. Only the first gives a wrong label.
3. **Use a match radius that grows with apparent size:**
   `max(g_min, k × max(apparent_width_px, apparent_height_px))`. A fixed radius in pixels is wrong across
   the frame, because the same vehicle looks several times larger at one edge of a steep oblique view
   than at the other. You choose `g_min` and `k`; we set no values.
4. **Assign all the pairs at once, not nearest first.** Use a minimum-cost assignment over every pair
   inside the radius. Nearest-first matching in dense traffic gives one vehicle's label to another
   vehicle's track.
5. **Keep the evidence with each label you carry over:** the pixel distance, the distance divided by
   apparent size, and how much closer the chosen vehicle was than the next-best one. Then a wrong match
   can be found later instead of looking like a hard example.

## Carrying labels across time

- **A label goes on a stretch of one track, seen by one camera, inside one label interval.** One
  vehicle can become several tracks (a lost and re-found track, an id switch), so labels never go on
  "the vehicle" as a whole.
- **Clip at the interval's edges.** A track that runs past the start or end of a label interval gets the
  label only for the stills inside it. The interval edges are in the manifest, not in the sidecars: the
  `instance` rows declare each labeled behavior and the vehicles taking part, and the `interval_opened` and
  `interval_closed` rows give each phase's start and end as `sim_time_s`. Compare them with each still's
  `sim_time_s`.
- **Each camera is a separate observation.** With several cameras, one label interval can give a
  labeled stretch on each camera's tracks, overlapping in time. That is correct; reconciling them is up
  to you.
- **A track that follows more than one vehicle over its life should not carry one vehicle's label.**
  Decide for yourself how large a share of a track one vehicle must account for.

## What a capture folder does not contain

No matching tool, no match results, no match quality numbers, and no scores of any kind. Those depend
on a model's output, and they are yours to make.
