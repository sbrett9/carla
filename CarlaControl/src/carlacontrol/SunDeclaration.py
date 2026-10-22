"""The scenario's epoch and the run's sun policy, as carla-drive hands them to its session."""
from __future__ import annotations

import argparse
import json
import os

import carlanet  # noqa: F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.CoSim import ScenarioLockCheck

from carlacontrol.ScenarioEpoch import ScenarioEpoch, ScenarioEpochRefusedError
from carlacontrol.ScenarioPackage import ScenarioPackage
from carlacontrol.SchemaPublication import SchemaPublication

# The illumination fields that belong to particular policies, with the policies that take each.
# IlluminationPolicy refuses one under any other policy.
_POLICY_FIELDS = {
    "rate_sun_s_per_sim_s": ("advance",),
    "freeze_at_civil_time": ("freeze_at",),
    "freeze_date_advances": ("freeze_at_window_start", "freeze_at"),
}


class SunDeclaration:
    """The scenario's epoch, the instant a run moves it to, and the run's sun policy, as the session
    is handed them.

    The session reads them and is the one judge of each; this gathers them, and refuses an epoch that
    does not conform to the published epoch schema (`epoch.schema.json`) before the session sees it,
    in the schema's words. An epoch file may be the `epoch` object on its own or a whole
    scenario.json, whose `illumination` object is then the policy. A policy given in the file and on
    the command line is refused rather than resolved: which of two declarations wins is the operator
    surface's decision to make, not carla-drive's.

    `--epoch-at` moves simulated second zero of the epoch the scenario was compiled against, which the
    compile lock beside `--scenario` records. Without `--epoch`, the epoch moved is the lock's, and the
    lock's `illumination` is the policy, each flag given replacing the field it names, as carla-capture
    lets an operator override a scenario's illumination default. A lock field that belongs to a policy
    other than the one a flag names is dropped; a flag's own is passed on, for the session to refuse
    by name. With `--epoch`, the file's epoch must be the lock's, by digest. The move is refused here,
    before anything connects, where the session would refuse it: on a scenario with no lock, which was
    compiled against no epoch, with another epoch than the lock's, and at an instant that is not a UTC
    date and time.
    """

    def __init__(self, args: argparse.Namespace) -> None:
        self.epoch: dict | None = None
        self.epoch_at: str | None = args.epoch_at
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
            self._refuse_unless_it_conforms(self.epoch, source)

        from_command_line = self.from_command_line(args)
        if from_file is not None and from_command_line is not None:
            raise SystemExit(
                f"{args.epoch} declares an illumination object and the command line declares "
                "another. Declare it in one place.")
        self.illumination = from_file if from_file is not None else from_command_line
        if self.epoch_at is not None:
            self._move(args, from_command_line)

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

    @staticmethod
    def over_the_lock(locked: dict | None, flags: dict | None) -> dict | None:
        """The lock's illumination object with each field the flags give in place of its own, or
        either alone where the other is None.

        A lock field that belongs to a policy other than the one the flags name is dropped, since the
        flags replaced the policy it belongs to. A flag's own is kept, as `from_command_line` keeps it.
        """
        if flags is None or locked is None:
            return flags if locked is None else locked
        merged = dict(locked)
        policy = flags.get("policy")
        if policy is not None:
            for name, policies in _POLICY_FIELDS.items():
                if policy not in policies:
                    merged.pop(name, None)
        merged.update(flags)
        return merged

    def _move(self, args: argparse.Namespace, from_command_line: dict | None) -> None:
        """Check the move `--epoch-at` asks for against the compile lock, and take the epoch to move,
        and its illumination, from the lock where `--epoch` names none."""
        lock_path = str(ScenarioLockCheck.LockPathFor(args.scenario))
        if not os.path.isfile(lock_path):
            raise SystemExit(
                f"--epoch-at {self.epoch_at} moves simulated second zero of the epoch the scenario was "
                f"compiled against, and {args.scenario} has no compile lock at {lock_path}: an "
                "uncompiled scenario was compiled against none. Give --epoch FILE declaring the epoch "
                "at the instant wanted, without --epoch-at.")
        try:
            package = ScenarioPackage(lock_path)
        except (OSError, ValueError) as unreadable:
            raise SystemExit(f"--epoch-at: the compile lock {lock_path} cannot be read: "
                             f"{unreadable}") from None
        if self.epoch is None:
            if not isinstance(package.epoch, dict):
                raise SystemExit(f"--epoch-at: the compile lock {lock_path} records no epoch to move. "
                                 "Recompile the scenario, or give --epoch FILE.")
            self.epoch = package.epoch
            self._refuse_unless_it_conforms(self.epoch, f"the epoch object of {lock_path}")
            # An empty object is the lock's record of a specification that declares no policy.
            self.illumination = self.over_the_lock(package.illumination or None, from_command_line)
            source = f"the compile lock {lock_path}"
        else:
            source = args.epoch
        try:
            compiled = ScenarioEpoch.read(self.epoch)
        except ScenarioEpochRefusedError as refused:
            raise SystemExit(f"--epoch-at: the epoch of {source} is refused: {refused}") from None
        if args.epoch and compiled.digest != package.epoch_block_sha256:
            raise SystemExit(
                f"--epoch-at moves the epoch {args.scenario} was compiled against, and {args.epoch} "
                f"declares another: its epoch digests as {compiled.digest}, and the compile lock "
                f"{lock_path} records {package.epoch_block_sha256}. Leave --epoch out to move the "
                "lock's epoch, or give the scenario's own scenario.json.")
        try:
            compiled.moved_to(self.epoch_at, "--epoch-at")
        except ValueError as refused:
            raise SystemExit(str(refused)) from None

    @staticmethod
    def _refuse_unless_it_conforms(epoch: object, source: str) -> None:
        """Exit naming every place an epoch departs from the published schema, in the schema's words."""
        problems = ScenarioEpoch.schema_problems(epoch)
        if problems:
            raise SystemExit(SchemaPublication.refusal(source, ScenarioEpoch.schema(), problems))
