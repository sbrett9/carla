"""carla-drive's live Cursor-on-Target feed: the events its session builds, sent one per UDP datagram."""
from __future__ import annotations

import argparse
import json
import logging
import socket
import time
import unicodedata

from carlacontrol.CotDisplayConvention import COT_AFFILIATIONS, CotDisplayConvention
from carlacontrol.CotUdpEmitter import CotUdpEmitter

logger = logging.getLogger(__name__)


class DriveCotFeed:
    """Sends a SUMO drive's live track as Cursor-on-Target, one event per UDP datagram.

    The session builds the events (`world.start_sumo_drive(..., live_track=...)`): on each sampled
    frame, one per body the frame drew, each the event a capture of that frame writes for the vehicle
    in its truth sidecar, less what depends on a picture. This gives the session the feed's settings
    and the display convention's affiliations (`live_track`), takes the samples the session completed
    after each advance and sends each event's XML as the session wrote it.

    A send that fails is counted and the drive goes on. The first failure of a run of them is logged,
    and so is the send that next succeeds.
    """

    DEFAULT_PORT = 6969
    DEFAULT_TTL = 1
    DEFAULT_RATE_HZ = 1.0
    DEFAULT_STALE_S = 3.0
    DEFAULT_UID_PREFIX = "CARLA-TRUTH"
    DEFAULT_AFFILIATION = "n"

    def __init__(self, args: argparse.Namespace) -> None:
        """Read the feed's settings, with every default already resolved, and its display convention.

        Raises:
            ValueError: the host does not resolve, or the convention file cannot be read or is not a
                display convention; the message says which.
        """
        self.host, self.port = self.address(args.cot_udp)
        try:
            # Looked up once here: CotUdpEmitter hands its host to every sendto, which would otherwise
            # look a name up again for every datagram.
            self.ip = socket.gethostbyname(self.host)
        except OSError as unresolved:
            raise ValueError(f"--cot-udp: the host {self.host} does not resolve to an IPv4 address: "
                             f"{unresolved}") from unresolved
        self.ttl = args.cot_udp_ttl
        self.rate_hz = args.cot_hz
        self.stale_s = args.cot_stale
        self.uid_prefix = args.cot_uid_prefix
        self.affiliation = args.cot_affiliation
        self.convention_beside = CotDisplayConvention.beside(args.scenario)
        self.convention_path = CotDisplayConvention.given_or_beside(args.cot_display_convention,
                                                                    args.scenario)
        self.convention_found = self.convention_path is not None and args.cot_display_convention is None
        self.convention = CotDisplayConvention({})
        if self.convention_path is not None:
            try:
                self.convention = CotDisplayConvention.from_file(self.convention_path)
            except (OSError, ValueError) as unread:
                raise ValueError(f"the display convention {self.convention_path} cannot be read: "
                                 f"{unread}") from unread
        self.emitter = None
        self.track = None
        self.samples = 0
        self.events_sent = 0
        self.sends_failed = 0
        self.failing = False
        self.drains = 0
        self.drain_seconds = 0.0
        self.worst_drain_seconds = 0.0

    @classmethod
    def address(cls, text: str) -> tuple[str, int]:
        """The host and port `--cot-udp` names: HOST:PORT, or HOST alone for port 6969.

        Raises:
            ValueError: the text is not HOST or HOST:PORT, or the port is outside 1 to 65535.
        """
        host, colon, port_text = text.partition(":")
        if not host or any(character.isspace() for character in host):
            raise ValueError(f"'{text}' is not HOST:PORT: it names no host")
        if not colon:
            return host, cls.DEFAULT_PORT
        if not (port_text.isascii() and port_text.isdigit()):
            raise ValueError(f"'{text}' is not HOST:PORT: the port '{port_text}' is not a whole number")
        port = int(port_text)
        if not 1 <= port <= 65535:
            raise ValueError(f"'{text}' names port {port}, outside 1 to 65535")
        return host, port

    @staticmethod
    def uid_prefix_problem(prefix: str) -> str | None:
        """Why `prefix` cannot begin a uid, by the rule the session holds it to, or None where it can."""
        if prefix and not any(character.isspace() or unicodedata.category(character) == "Cc"
                              for character in prefix):
            return None
        return f"'{prefix}' must be one or more characters, none of them whitespace"

    @staticmethod
    def affiliation_problem(letter: str) -> str | None:
        """Why `letter` cannot be a CoT affiliation, or None where it can."""
        if letter in COT_AFFILIATIONS:
            return None
        return (f"'{letter}' must be one of {' '.join(sorted(COT_AFFILIATIONS))}, the letters a CoT "
                "type's affiliation takes")

    def live_track(self) -> dict:
        """The session's `live_track`: the rate, staleness, uid prefix and affiliations, each given."""
        return {"rate_hz": self.rate_hz, "stale_s": self.stale_s, "uid_prefix": self.uid_prefix,
                "affiliation": self.affiliation,
                "affiliation_by_type": dict(self.convention.affiliation_by_type)}

    def start(self, session) -> None:
        """Open the socket, once the session that builds the events has started."""
        self.emitter = CotUdpEmitter(self.ip, self.port, ttl=self.ttl)
        self.track = session.LiveTrack

    def after_step(self) -> None:
        """Send what the session sampled during the advance just made."""
        if self.track is not None:
            self._drain()

    def close(self) -> None:
        """Send the samples still waiting, log what the feed did, and close the socket."""
        if self.track is None:
            return
        try:
            self._drain()
            self._summarise()
        finally:
            self.emitter.close()
            self.track = None

    def target(self) -> str:
        """Where the feed goes, as the log names it: the address, and the name it was looked up from."""
        if self.ip == self.host:
            return f"{self.ip}:{self.port}"
        return f"{self.host} ({self.ip}):{self.port}"

    def describe_convention(self) -> str:
        """The display convention as the launch states it: which file, or none, and the default."""
        if self.convention_path is None:
            return (f"no display convention: none given and no {self.convention_beside.name} beside "
                    f"the scenario; every vehicle takes {self.affiliation}")
        found = ", found beside the scenario" if self.convention_found else ""
        return (f"display convention {self.convention_path}{found}: {len(self.convention)} "
                f"populations; any other takes {self.affiliation}")

    def _drain(self) -> list[dict]:
        """Take the samples the session completed and send their events. Returns the samples taken."""
        started = time.perf_counter()
        samples = json.loads(str(self.track.TakeSamplesJson()))
        for sample in samples:
            self._send(sample)
        if samples:
            seconds = time.perf_counter() - started
            self.drains += 1
            self.drain_seconds += seconds
            self.worst_drain_seconds = max(self.worst_drain_seconds, seconds)
        return samples

    def _send(self, sample: dict) -> None:
        """Send each of one sample's events as one datagram."""
        self.samples += 1
        for event in sample["events"]:
            try:
                self.emitter.send(event["xml"])
            except OSError as failure:
                self.sends_failed += 1
                if not self.failing:
                    self.failing = True
                    logger.warning("cot feed: a send to %s failed (%s); failed sends are counted and "
                                   "the drive goes on", self.target(), failure)
                continue
            self.events_sent += 1
            if self.failing:
                self.failing = False
                logger.info("cot feed: sending to %s again, %d sends having failed so far",
                            self.target(), self.sends_failed)

    def _summarise(self) -> None:
        """Log what the feed sent and what it could not."""
        track = self.track
        mean = self.drain_seconds / self.drains if self.drains else 0.0
        trouble = (self.sends_failed or track.FramesUnpaired or track.BodiesMissing
                   or track.SamplesDropped)
        (logger.warning if trouble else logger.info)(
            "cot feed           %d samples, %d events sent to %s, %d sends failed; %d sampled frames "
            "had no truth, %d drawn bodies no record, %d samples were dropped untaken; drained in "
            "%.4f s on average, %.4f s at worst", self.samples, self.events_sent, self.target(),
            self.sends_failed, track.FramesUnpaired, track.BodiesMissing, track.SamplesDropped, mean,
            self.worst_drain_seconds)
