"""The server's refusal of a control write while a SUMO drive holds the world's drive lease.

A SUMO drive session takes the lease on the server before SUMO starts (`CarlaNet.CoSim.DriveLease`).
While it holds it the server refuses `set_actor_autopilot` (enabling), `apply_control_to_vehicle`,
`apply_ackermann_control_to_vehicle` and `apply_physics_control`, direct or in a batch, for every
actor and every client, each with the words `CarlaServer.cpp`'s `DriveLeaseRefusal` writes:

    <call>: refused while <holder> holds the drive lease on this world; no other traffic drives a
    vehicle here until the holder releases it (release_drive_lease), the world is reloaded, or the
    lease is broken (break_drive_lease)

This is how a traffic tool on the Python side recognises that refusal and names the holder from it,
so the operator reads who to stop rather than an error per vehicle. The mark is held equal to the
.NET traffic manager's (`TrafficManagerLocal.DriveLeaseRefusalMark`) by `test_drive_lease_lockout`.
"""
from __future__ import annotations

# The text every such refusal carries, whichever of the four calls was refused.
REFUSAL_MARK = "holds the drive lease on this world"

_LEAD_IN = "refused while "


def refusal(failure) -> str | None:
    """The server's words where `failure` is a control write it refused because a drive lease is
    held, or None where it is anything else. Takes an exception, a .NET exception (read by its
    `Message`) or a batch response's error text."""
    text = str(getattr(failure, "Message", None) or failure)
    return text if REFUSAL_MARK in text else None


def holder_named(words: str) -> str:
    """The holder a refusal names: the words between "refused while " and the mark. Where the words
    are not there to read, the whole text, so nothing is invented."""
    head, mark, _ = words.partition(REFUSAL_MARK)
    if not mark:
        return words
    _, lead, holder = head.rpartition(_LEAD_IN)
    return holder.strip() if lead else head.strip()


def locked_out_by(failure) -> str | None:
    """The holder the server names where `failure` is a refused control write, or None."""
    words = refusal(failure)
    return None if words is None else holder_named(words)
