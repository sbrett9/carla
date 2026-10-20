"""The scenario's epoch and the run's sun policy, as carla-drive hands them to its session."""
from __future__ import annotations

import argparse
import json

from carlacontrol.ScenarioEpoch import ScenarioEpoch
from carlacontrol.SchemaPublication import SchemaPublication


class SunDeclaration:
    """The scenario's epoch and the run's sun policy, as the session is handed them.

    The session reads both and is the one judge of either; this gathers them, and refuses an epoch
    that does not conform to the published epoch schema (`epoch.schema.json`) before the session
    sees it, in the schema's words. An epoch file may be the `epoch` object on its own or a whole
    scenario.json, whose `illumination` object is then the policy. A policy given in the file and on
    the command line is refused rather than resolved: which of two declarations wins is the operator
    surface's decision to make, not carla-drive's.
    """

    def __init__(self, args: argparse.Namespace) -> None:
        self.epoch: dict | None = None
        self.illumination: dict | None = None
        from_file: dict | None = None
        if args.epoch:
            with open(args.epoch, encoding="utf-8") as handle:
                document = json.load(handle)
            source = args.epoch
            if isinstance(document, dict) and isinstance(document.get("epoch"), dict):
                self.epoch = document["epoch"]
                from_file = document.get("illumination")
                source = f"the epoch object of {args.epoch}"
            else:
                self.epoch = document
            problems = ScenarioEpoch.schema_problems(self.epoch)
            if problems:
                raise SystemExit(SchemaPublication.refusal(source, ScenarioEpoch.schema(),
                                                           problems))

        from_command_line = self.from_command_line(args)
        if from_file is not None and from_command_line is not None:
            raise SystemExit(
                f"{args.epoch} declares an illumination object and the command line declares "
                "another. Declare it in one place.")
        self.illumination = from_file if from_file is not None else from_command_line

    @staticmethod
    def from_command_line(args: argparse.Namespace) -> dict | None:
        """The `illumination` object the flags describe, or None where none of them was given.

        Every flag given is passed on, including one that belongs to another policy, so the session
        refuses the combination by name rather than carla-drive quietly dropping half of it.
        """
        given = (args.illumination, args.solar_rate, args.freeze_at)
        if all(value is None for value in given) and not (
                args.freeze_date_advances or args.no_sun_required):
            return None
        declared: dict = {"illumination_version": 1}
        if args.illumination is not None:
            declared["policy"] = args.illumination
        if args.solar_rate is not None:
            declared["rate_sun_s_per_sim_s"] = args.solar_rate
        if args.freeze_at is not None:
            declared["freeze_at_civil_time"] = args.freeze_at
        if args.freeze_date_advances:
            declared["freeze_date_advances"] = True
        if args.no_sun_required:
            declared["require_sun"] = False
        return declared
