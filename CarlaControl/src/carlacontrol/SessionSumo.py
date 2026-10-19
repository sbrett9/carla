"""Which SUMO installation carla-drive names for its session."""
from __future__ import annotations

import logging
import os

from carlacontrol.ToolLayout import ToolLayout

# Where the repository's own SUMO builds are looked for: the checkout this carlacontrol was
# imported from, or, installed, nowhere.
LAYOUT = ToolLayout.current()

logger = logging.getLogger(__name__)


class SessionSumo:
    """Which SUMO installation carla-drive names for the session, and the rule that chose it.

    Named rather than left to the session's own search, because that search finds the repository by
    walking upward from the CarlaNet assemblies, and assemblies loaded from an installed wheel sit in
    site-packages with no repository above them: the walk finds nothing and `SUMO_HOME` decides --
    on a machine with a system-wide SUMO, a different release from the one that converted the world.
    The rules and their order are the session's own (`CarlaNet.Sumo.SumoInstallation`):
    `CARLANET_SUMO_HOME`, the repository's staged build, its source build. `--sumo-home` goes ahead
    of all of them and is passed on as given, so a directory with no SUMO in it is refused by the
    session by name rather than skipped here. Where no rule finds a `sumo`, nothing is named and the
    session searches `SUMO_HOME`, then PATH.

    This chooses the installation and nothing else. Whether its release is the world's converter is
    decided by the session, which is the one place that comparison is made.
    """

    OVERRIDE_VARIABLE = "CARLANET_SUMO_HOME"
    EXECUTABLE = "sumo.exe" if os.name == "nt" else "sumo"
    # The checkout's own builds, staged then source; none when carlacontrol is installed.
    REPOSITORY_BUILDS = tuple((rule, str(home)) for rule, home in LAYOUT.repository_sumo_builds())

    def __init__(self, explicit: str | None) -> None:
        self.home: str | None = None
        self.rule = "no rule found one"
        if explicit:
            self.home, self.rule = explicit, "--sumo-home"
            return
        candidates = ((self.OVERRIDE_VARIABLE, os.environ.get(self.OVERRIDE_VARIABLE)),
                      *self.REPOSITORY_BUILDS)
        for rule, home in candidates:
            if home and os.path.isfile(os.path.join(home, "bin", self.EXECUTABLE)):
                self.home, self.rule = os.path.abspath(home), rule
                return

    def announce(self, gui: bool) -> None:
        """Say which installation is being named, and which of its binaries, before anything is
        started. Whether the installation has a `sumo-gui` is the session's to say."""
        binary = "sumo-gui in place of sumo" if gui else "sumo"
        if self.home is None:
            logger.info("SUMO: none named -- no --sumo-home, %s holds none and %s -- so the "
                        "session searches SUMO_HOME, then PATH, and launches %s",
                        self.OVERRIDE_VARIABLE,
                        "there is no repository build, carlacontrol being installed"
                        if LAYOUT.installed else "this repository has no build under Build/",
                        binary)
        else:
            logger.info("SUMO: naming %s for the session (%s), which launches %s", self.home,
                        self.rule, binary)
