# Matching your tracks to the truth

This page is for teams that receive capture folders from SUMO-driven captures and want to carry the captures' labels onto their own detector's tracks.

**The files hold labels and truth, never scores or verdicts.**\
Matching a track to a truth vehicle is your job.\
It needs your detector's output.\
No part of this system ever sees that output.

So this system does two things only.\
It writes truth in a form that can be matched by position and time.\
It also publishes the rule below so every team matches the same way.\
Nothing in a capture folder depends on a model having run.

[What a capture folder holds](../EPOL/Capture_Folder.md) explains the files.\
[Behavioral annotations](../EPOL/Behavioral_Annotations.md) explains the labels.\
Every field named below is described on its schema page: [Truth sidecar](../Schemas/Truth_Sidecar.md), [Run manifest](../Schemas/Run_Manifest.md) and [World truth track](../Schemas/World_Truth_Track.md).

## What you match against

A capture folder holds one folder per camera, with a PNG and a truth sidecar (`.xml`) of the same name for every still.\
It also holds a `truth/` folder with the run manifest (`manifest.jsonl`) and the world truth track (`world_truth_track.csv`).\
Each sidecar describes its own still's frame, numbered by its `tick`.\
A still is never written with truth from another frame.

A sidecar lists every vehicle its frame drew, most of them outside the picture.\
Match only against the records whose `in_frame` is `wholly` or `partly`.\
Only those records carry a box.\
In the sidecar's `<_carla>` element:

- `box_px` is the upright box in this still's pixels (x min, y min, x max, y max).\
  `box_oriented_px` is the box turned with the vehicle (four corners).\
  Both are already projected through this still's own camera pose and intrinsics, which the sidecar also records.\
  Neither is clipped to the picture.\
  `truncation` is the share of `box_px` outside it.
- `apparent_width_px` and `apparent_height_px`, how large the vehicle appears.
- `occlusion` and `occlusion_level`, or `occlusion_unmeasured` where occlusion was not measured.
- `camera_range_m`.
- `beyond_draw_distance`, where the run drew vehicles only out to a set distance and this one was beyond it.\
  A vehicle `wholly` beyond it is in the truth and not in the picture.

Beside `<_carla>`, the eight corners of the 3D box are in `<_box3d frame="geodetic">`.\
The author's labels for that frame sit beside it too, in `<_supervision>`.

## The rule, per camera and per still

1. **Match by position and time, never by id.**\
   The ids in the truth files (`uid`, which is `CARLA-TRUTH-SUMO-<sumo_id>`, `sumo_id` and `actor_id`) are for joining truth to truth.\
   Your tracks carry none of them.\
   If one of these ids ever turns up in your detector's output, it leaked from the capture folders you received.\
   Please tell whoever sent them.\
   `actor_id` does not even name one vehicle: a CARLA body draws a series of vehicles over a run.
2. **Compare in the image, not on the ground.**\
   Take the distance in pixels between your box's center and the truth record's `box_px` center.\
   Divide it by the truth vehicle's apparent size.\
   A distance on the ground mixes up two different mistakes: matching the wrong vehicle and locating the right vehicle poorly.\
   Only the first gives a wrong label.
3. **Use a match radius that grows with apparent size:** `max(g_min, k × max(apparent_width_px, apparent_height_px))`.\
   A fixed radius in pixels is wrong across the frame.\
   In a steep oblique view, the same vehicle looks several times larger at one edge than at the other.\
   You choose `g_min` and `k`.\
   The rule sets no values for them.
4. **Assign all the pairs at once, not nearest first.**\
   Use a minimum-cost assignment over every pair inside the radius.\
   Nearest-first matching in dense traffic gives one vehicle's label to another vehicle's track.
5. **Keep the evidence with each label you carry over.**\
   Keep the pixel distance and the distance divided by apparent size.\
   Also keep how much closer the chosen vehicle was than the next-best one.\
   Then a wrong match can be found later instead of looking like a hard example.

Appearance does not separate vehicles reliably.\
Every vehicle drawn with one body has the same color.\
The bodies are few (see [The vehicles behind the truth](../EPOL/Vehicle_Catalogue.md)).

In the sample capture `cap-20261008-041347-270d6d`, the still at frame 152208 shows two Ford Mustangs, both of color `0,0,0`.\
One, `dweller`, is parked at the curb and labeled `check:kerbside_dwell`.\
The other, `transit`, drives past and is labeled `check:through_transit`.\
Only their positions tell them apart.

## Carrying labels across time

- **A label goes on a stretch of one track, seen by one camera, inside one label interval.**\
  A label interval is the span of time a label is in force.\
  One vehicle can become several tracks (a lost and re-found track, an id switch).\
  So labels never go on "the vehicle" as a whole.
- **Clip at the interval's edges.**\
  A track that runs past the start or end of a label interval gets the label only for the stills inside it.\
  Each sidecar's `<_supervision>` already says which labels were in force for each vehicle on that still.\
  So the stills a label covers are the stills whose matched record carries it.

  The exact edges, which fall between stills, are in the manifest.\
  Its `instance` rows declare each labeled behavior and the vehicles taking part.\
  Its `interval_opened` and `interval_closed` rows give each interval's start and end as `sim_time_s`.
- **Compare times on SUMO's clock, through the frame number.**\
  The manifest's `sim_time_s` is SUMO's clock.\
  A still's own `sim_time_s` is CARLA's, with another zero.\
  In the sample capture, it reads 235.299 s more for every still.

  Put a still on SUMO's clock by its `tick`.\
  Find the world truth track's row with that `frame`.\
  Then read that row's `sim_time_s`.\
  A label holds from the frame at its interval's start up to, but not including, the frame at its end.\
  See [How the stills line up with the manifest's intervals](../EPOL/Capture_Folder.md#how-the-stills-line-up-with-the-manifests-intervals).
- **Each camera is a separate observation.**\
  With several cameras, one label interval can give a labeled stretch on each camera's tracks.\
  Those stretches overlap in time.\
  That is correct.\
  Reconciling them is up to you.
- **Do not give one vehicle's label to a track that follows more than one vehicle over its life.**\
  Decide for yourself how large a share of a track one vehicle must account for.

## What a capture folder does not contain

No matching tool, no match results, no match quality numbers and no scores of any kind.\
Those depend on a model's output.\
They are yours to make.
