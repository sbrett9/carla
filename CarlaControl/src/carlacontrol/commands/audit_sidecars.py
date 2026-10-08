"""Fail if a SUMO drive's truth sidecars list parked bodies as vehicles or lose track of who is who.

Reads every truth sidecar under a capture directory -- one channel's, or a whole capture's -- and
counts the vehicle records standing below the ground band the moving vehicles draw (the pool's
parked bodies, about 300 m under the ground), the records that carry no SUMO vehicle id, the uids
that name more than one SUMO vehicle over the capture, and the SUMO vehicles that appear under more
than one uid. See `carlacontrol.TruthSidecarAudit` for how the band is drawn. A sidecar is known by
what it holds, not by its name -- `<camera name>_<capture time>.xml`, or `SCTMV_<capture time>.xml`
from before cameras were named -- so a directory holding either, or several cameras' stills, is read
whole.

Where the run had a supervision plan -- a sidecar names one, or says its supervision was unknown --
every SUMO vehicle record must carry a `<_supervision>` in one of the three states, consistent with
the instances it names; a sidecar saying nothing of supervision in such a run is a defect too, and so
is a supervision element outside a vehicle's record, which would be a label with no vehicle. Sidecars
whose supervision was unknown are counted, not faulted: the run's closeout gates them.

Every vehicle record must say whether the vehicle is in the camera's picture (`in_frame`: `wholly`,
`partly`, `none` or `behind_camera`) and, where its five occlusion fields are absent, why
(`occlusion_unmeasured`); occlusion fields on a vehicle the picture has no view of, a record with
neither occlusion nor a reason, a reason beside a measurement, and a place or reason outside the
recorder's words are defects.

Every vehicle record in the picture (`wholly` or `partly`) must carry its box -- `box_px`,
`box_oriented_px`, `truncation`, `pitch_deg`, `roll_deg`, `camera_range_m` and a
`<_box3d frame="geodetic">` of eight corners -- and no record outside the picture or behind the lens may
carry any of it, `camera_range_m` beside `beyond_draw_distance` apart.

Every vehicle record in the picture must also carry its `lights` -- the lights commanded on for it in
words, or `none` -- and every SUMO vehicle record in it its `pose_source` -- `sumo`, `interpolated`,
`jump` or `stale` -- unless its sidecar says `lights="unknown"` or `pose_source="unknown"`, because the snapshot of
its frame did not carry them; those sidecars are counted, not faulted, as the run's closeout gates them.
Either on a record outside the picture, a `pose_source` on a record naming no SUMO vehicle, and a word
outside the recorder's are defects.

Every sidecar of a camera whose captures carry its exposure must carry it too, and the same one: the
platform event's `<_carla_exposure>` -- `post_process_profile`, `method`, `iso`, `shutter_s`, `fstop`,
`compensation_ev`, and `ev100` under manual alone. A sidecar of such a camera without it, an element
missing a field or holding another method, an `ev100` under histogram or none under manual, and a
camera carrying two exposures are defects. A camera that carries it on none of its captures, one of a
server built before the camera published its exposure, is not.

Any of those is a defect and the exit status is 1. A capture of traffic-manager traffic carries no
SUMO id by design: pass `--traffic-manager` and only the ground band, the uid and the in-picture
checks apply.

Examples (from a checkout, `python CarlaControl/scripts/audit_truth_sidecars.py` is the same tool):
    carla-audit-sidecars Build/captures/cap-20260928-210156-b06714
    carla-audit-sidecars Build/captures/cap-.../OVERWATCH-1 --margin 80
    carla-audit-sidecars Build/captures/sctmv_run --traffic-manager
"""
import argparse
import logging
import os
import sys
from pathlib import Path

from carlacontrol.TruthSidecarAudit import (
    DEFAULT_MARGIN_M,
    TruthSidecarAudit,
)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog=os.path.basename(sys.argv[0]), description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("capture", type=Path,
                        help="a capture directory, or one channel's directory within it")
    parser.add_argument("--margin", type=float, default=DEFAULT_MARGIN_M,
                        help="metres below the lowest moving vehicle a record may stand and still "
                             f"be on the ground (default {DEFAULT_MARGIN_M:g})")
    parser.add_argument("--floor-hae", type=float, default=None,
                        help="the lowest plausible bare-earth hae in metres, stated outright, for "
                             "a capture with no moving vehicle to draw the band from")
    parser.add_argument("--traffic-manager", action="store_true",
                        help="the capture is of traffic-manager traffic, whose records carry no "
                             "SUMO id by design")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    audit = TruthSidecarAudit(margin_m=args.margin, floor_hae=args.floor_hae)
    sidecars = audit.sidecars(args.capture)
    if not sidecars:
        logging.error("no truth sidecar under %s", args.capture)
        return 2

    result = audit.audit(sidecars)
    for line in TruthSidecarAudit.describe(result):
        logging.info("%s", line)

    defects = result.defects(sumo_drive=not args.traffic_manager)
    for defect in defects:
        logging.error("DEFECT: %s", defect)
    if defects:
        return 1
    logging.info("every vehicle record stands on the ground, says whether it is in the picture, carries "
                 "its box, lights and pose source where it is"
                 + (", every capture of a camera with an exposure carries it"
                    if result.cameras_with_exposure else "")
                 + (" and names one SUMO vehicle under one uid" if not args.traffic_manager else "")
                 + (", and carries its supervision" if result.had_plan else ""))
    return 0
