"""PackageWorld records a distribution's release before it cooks, under the name the base cook gives
it, and refuses anything that would cook a world the distribution cannot load.

`-Distribution` (PackageWorld.ps1) and `--distribution` (PackageWorld.sh) read the distribution's
VERSION, name the release as the base cook names it -- `git rev-parse --short` of the CARLA commit,
the form `git log -1 --format=%h` gives -- and copy the distribution's
`CarlaServer/CarlaUnreal/AssetRegistry.bin` to `Unreal/CarlaUnreal/Releases/<release>/<platform>/`,
then cook as before. Both launchers are run here against a throwaway checkout (a git repository
holding an exported world) and a throwaway distribution, with a stand-in for the engine's RunUAT that
records its arguments and fails with a status of its own, so the cook is reached but never run.

Checked: the release is recorded byte for byte at the right path and handed to the cook with the
distribution's configuration; an identical record already there is reused. Refused, with nothing
recorded and nothing cooked: a checkout at another commit, a VERSION missing or naming no commit, a
distribution with no asset registry, one for the other platform, a configuration or a release given
that contradicts the distribution's, a different registry already recorded under the release, and a
release folder holding only a registry from a local cook. And without a checkout around it -- as in
a distribution's world-tools folder -- the script asks for one.

Skipped where PowerShell 7 (pwsh) or a bash that can read this machine's paths is not installed.
"""
from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
WINDOWS_SCRIPT = _REPO / "Scripts" / "Windows" / "PackageWorld.ps1"
LINUX_SCRIPT = _REPO / "Scripts" / "Linux" / "PackageWorld.sh"
WORLD = "Fixture_World"
UAT_STATUS = 7      # the stand-in RunUAT's exit status: the cook was reached
OTHER_COMMIT = "1234567890abcdef1234567890abcdef12345678"


def _bash() -> str | None:
    """A bash that understands this machine's paths. On Windows that is Git for Windows' bash, beside
    git, and not the system's bash.exe, which runs WSL and cannot see a Windows path."""
    if os.name != "nt":
        return shutil.which("bash")
    git = shutil.which("git")
    if git is None:
        return None
    # git.exe is in <Git>\cmd\ or <Git>\mingw64\bin\, depending on which PATH found it.
    for folder in Path(git).resolve().parents[:3]:
        candidate = folder / "bin" / "bash.exe"
        if candidate.is_file():
            return str(candidate)
    return None


PWSH = shutil.which("pwsh")
BASH = _bash()


@dataclass(frozen=True)
class Launcher:
    """One platform's PackageWorld, and how it spells its options and names its platform."""

    name: str
    platform: str           # the cook platform: the Releases/ folder name
    target: str             # the distribution folder's platform word
    server: str             # the file a distribution of this platform starts its server with
    other_server: str

    def command(self, *, world: str, carla_root: Path, engine: Path, distribution: Path | None,
                extra: dict[str, str]) -> list[str]:
        options = {"world": world, "carla-root": str(carla_root), "unreal-engine-root": str(engine)}
        if distribution is not None:
            options["distribution"] = str(distribution)
        options.update(extra)
        if self.name == "ps1":
            spelled = {"world": "-World", "carla-root": "-CarlaRoot",
                       "unreal-engine-root": "-UnrealEngineRoot", "distribution": "-Distribution",
                       "config": "-Config", "based-on-release": "-BasedOnRelease"}
            arguments = [part for key, value in options.items() for part in (spelled[key], value)]
            return [PWSH, "-NoProfile", "-NonInteractive", "-File", str(WINDOWS_SCRIPT), *arguments]
        arguments = [part for key, value in options.items()
                     for part in (f"--{key}", Path(value).as_posix() if os.sep in value else value)]
        return [BASH, LINUX_SCRIPT.as_posix(), *arguments]


LAUNCHERS = [
    pytest.param(Launcher("ps1", "Windows", "Win64", "CarlaUnreal.exe", "CarlaUnreal.sh"),
                 id="PackageWorld.ps1",
                 marks=pytest.mark.skipif(PWSH is None, reason="needs PowerShell 7 (pwsh)")),
    pytest.param(Launcher("sh", "Linux", "Linux", "CarlaUnreal.sh", "CarlaUnreal.exe"),
                 id="PackageWorld.sh",
                 marks=pytest.mark.skipif(BASH is None, reason="needs a bash that reads these paths")),
]


def _git(checkout: Path, *arguments: str) -> str:
    environment = {**os.environ, "GIT_AUTHOR_NAME": "fixture", "GIT_AUTHOR_EMAIL": "f@example.com",
                   "GIT_COMMITTER_NAME": "fixture", "GIT_COMMITTER_EMAIL": "f@example.com"}
    return subprocess.run(["git", "-C", str(checkout), *arguments], capture_output=True, text=True,
                          check=True, env=environment).stdout.strip()


@pytest.fixture
def checkout(tmp_path) -> Path:
    """A CARLA checkout in miniature: the project file, one world exported and marked for separate
    delivery, the world interface declaration, all committed."""
    root = tmp_path / "carla"
    project = root / "Unreal" / "CarlaUnreal"
    plugin = project / "Plugins" / "GeneratedWorlds" / WORLD
    plugin.mkdir(parents=True)
    (project / "CarlaUnreal.uproject").write_text("{}\n", encoding="utf-8")
    (plugin / f"{WORLD}.uplugin").write_text("{}\n", encoding="utf-8")
    (plugin / "DeliverSeparately.txt").write_text("", encoding="utf-8")
    (project / "Config").mkdir()
    (project / "Config" / "DefaultWorldInterface.ini").write_text(
        "[WorldInterface]\nMajor=1\nMinor=0\n", encoding="utf-8")
    _git(root, "init", "-q")
    _git(root, "add", "-A")
    _git(root, "commit", "-q", "-m", "fixture")
    return root


@pytest.fixture
def engine(tmp_path) -> Path:
    """An engine root whose RunUAT records its arguments and fails, so no cook ever runs."""
    batch = tmp_path / "UE" / "Engine" / "Build" / "BatchFiles"
    batch.mkdir(parents=True)
    (batch / "RunUAT.bat").write_text(
        f'@echo off\r\necho %* > "%~dp0uat-arguments.txt"\r\nexit /b {UAT_STATUS}\r\n',
        encoding="utf-8")
    (batch / "RunUAT.sh").write_text(
        f'#!/usr/bin/env bash\necho "$@" > "$(dirname "$0")/uat-arguments.txt"\nexit {UAT_STATUS}\n',
        encoding="utf-8", newline="\n")
    (batch / "RunUAT.sh").chmod(0o755)
    return tmp_path / "UE"


def _uat_arguments(engine: Path) -> str | None:
    recorded = engine / "Engine" / "Build" / "BatchFiles" / "uat-arguments.txt"
    return recorded.read_text(encoding="utf-8", errors="replace") if recorded.is_file() else None


def _distribution(tmp_path: Path, launcher: Launcher, commit: str, *, config: str = "Development",
                  registry: bytes = b"the base package's asset registry\x00\x01",
                  server: str | None = None, version: str | None = None) -> Path:
    root = tmp_path / f"Carla-0.10.0-{launcher.target}-{config}"
    (root / "CarlaServer" / "CarlaUnreal").mkdir(parents=True)
    (root / "CarlaServer" / (server or launcher.server)).write_text("", encoding="utf-8")
    if registry is not None:
        (root / "CarlaServer" / "CarlaUnreal" / "AssetRegistry.bin").write_bytes(registry)
    text = version if version is not None else (
        "Carla version:          0.10.0\n"
        "World interface:        1.0\n"
        f"Carla git hash:         {commit}\n"
        "Content git hash:       6bcd042a91a54d9a2f2f002869fbf1c75f3768f4\n"
        "UnrealEngine git hash:  e5e266de195a2400a6a74180402fb1a3e8f75472\n")
    (root / "VERSION").write_text(text, encoding="utf-8", newline="\n")
    return root


def _run(launcher: Launcher, checkout: Path, engine: Path, distribution: Path | None,
         **extra: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        launcher.command(world=WORLD, carla_root=checkout, engine=engine, distribution=distribution,
                         extra={key.replace("_", "-"): value for key, value in extra.items()}),
        capture_output=True, text=True, timeout=300, encoding="utf-8", errors="replace",
        check=False)


def _releases(checkout: Path) -> Path:
    return checkout / "Unreal" / "CarlaUnreal" / "Releases"


def _refused(completed: subprocess.CompletedProcess, engine: Path, checkout: Path,
             *said: str) -> None:
    output = completed.stdout + completed.stderr
    assert completed.returncode == 1, output
    for words in said:
        assert words in output, output
    assert _uat_arguments(engine) is None, "the cook ran after a refusal"


# -- recorded -----------------------------------------------------------------------------------------

@pytest.mark.parametrize("launcher", LAUNCHERS)
def test_the_release_is_recorded_under_the_base_cook_s_name_and_cooked_against(
        launcher, tmp_path, checkout, engine):
    commit = _git(checkout, "rev-parse", "HEAD")
    release = _git(checkout, "rev-parse", "--short", commit)
    assert release == _git(checkout, "log", "-1", "--format=%h")
    distribution = _distribution(tmp_path, launcher, commit.upper(), config="Shipping")

    completed = _run(launcher, checkout, engine, distribution)

    output = completed.stdout + completed.stderr
    assert completed.returncode == UAT_STATUS, output
    recorded = _releases(checkout) / release / launcher.platform / "AssetRegistry.bin"
    assert recorded.read_bytes() == \
        (distribution / "CarlaServer" / "CarlaUnreal" / "AssetRegistry.bin").read_bytes()
    assert sorted(path.name for path in _releases(checkout).iterdir()) == [release]
    cooked = _uat_arguments(engine)
    assert f"-BasedOnReleaseVersion={release}" in cooked
    # The configuration is the distribution's, read from its folder name.
    assert "-clientconfig=Shipping" in cooked


@pytest.mark.parametrize("launcher", LAUNCHERS)
def test_an_identical_record_is_reused(launcher, tmp_path, checkout, engine):
    commit = _git(checkout, "rev-parse", "HEAD")
    distribution = _distribution(tmp_path, launcher, commit)
    recorded = _releases(checkout) / _git(checkout, "rev-parse", "--short", commit) / \
        launcher.platform / "AssetRegistry.bin"
    recorded.parent.mkdir(parents=True)
    shutil.copyfile(distribution / "CarlaServer" / "CarlaUnreal" / "AssetRegistry.bin", recorded)

    completed = _run(launcher, checkout, engine, distribution)

    assert completed.returncode == UAT_STATUS, completed.stdout + completed.stderr
    assert "already recorded" in completed.stdout + completed.stderr


# -- refused ------------------------------------------------------------------------------------------

@pytest.mark.parametrize("launcher", LAUNCHERS)
def test_a_checkout_at_another_commit_is_refused(launcher, tmp_path, checkout, engine):
    distribution = _distribution(tmp_path, launcher, OTHER_COMMIT)
    completed = _run(launcher, checkout, engine, distribution)
    _refused(completed, engine, checkout, "not at the commit the distribution was built from",
             OTHER_COMMIT, _git(checkout, "rev-parse", "HEAD"))
    assert not _releases(checkout).exists()


@pytest.mark.parametrize("launcher", LAUNCHERS)
@pytest.mark.parametrize("version, said", [
    (None, "has no VERSION file"),
    ("Carla version:          0.10.0\n", "names no Carla git hash"),
])
def test_a_distribution_that_does_not_say_what_it_was_built_from_is_refused(
        launcher, tmp_path, checkout, engine, version, said):
    distribution = _distribution(tmp_path, launcher, _git(checkout, "rev-parse", "HEAD"),
                                 version=version or "")
    if version is None:
        (distribution / "VERSION").unlink()
    _refused(_run(launcher, checkout, engine, distribution), engine, checkout, said)
    assert not _releases(checkout).exists()


@pytest.mark.parametrize("launcher", LAUNCHERS)
def test_a_distribution_with_no_asset_registry_is_refused(launcher, tmp_path, checkout, engine):
    distribution = _distribution(tmp_path, launcher, _git(checkout, "rev-parse", "HEAD"),
                                 registry=None)
    _refused(_run(launcher, checkout, engine, distribution), engine, checkout,
             "carries no CarlaServer")
    assert not _releases(checkout).exists()


@pytest.mark.parametrize("launcher", LAUNCHERS)
def test_a_distribution_for_the_other_platform_is_refused(launcher, tmp_path, checkout, engine):
    distribution = _distribution(tmp_path, launcher, _git(checkout, "rev-parse", "HEAD"),
                                 server=launcher.other_server)
    _refused(_run(launcher, checkout, engine, distribution), engine, checkout,
             "distribution, and this script cooks for")
    assert not _releases(checkout).exists()


@pytest.mark.parametrize("launcher", LAUNCHERS)
def test_a_configuration_other_than_the_distribution_s_is_refused(launcher, tmp_path, checkout,
                                                                  engine):
    distribution = _distribution(tmp_path, launcher, _git(checkout, "rev-parse", "HEAD"))
    _refused(_run(launcher, checkout, engine, distribution, config="Shipping"), engine, checkout,
             "is a Development distribution")
    assert not _releases(checkout).exists()


@pytest.mark.parametrize("launcher", LAUNCHERS)
def test_a_release_other_than_the_distribution_s_is_refused(launcher, tmp_path, checkout, engine):
    distribution = _distribution(tmp_path, launcher, _git(checkout, "rev-parse", "HEAD"))
    _refused(_run(launcher, checkout, engine, distribution, based_on_release="6874d569b"), engine,
             checkout, "but the distribution's release is")
    assert not _releases(checkout).exists()


@pytest.mark.parametrize("launcher", LAUNCHERS)
def test_a_different_registry_already_recorded_is_refused_and_left_alone(launcher, tmp_path,
                                                                         checkout, engine):
    commit = _git(checkout, "rev-parse", "HEAD")
    distribution = _distribution(tmp_path, launcher, commit)
    recorded = _releases(checkout) / _git(checkout, "rev-parse", "--short", commit) / \
        launcher.platform / "AssetRegistry.bin"
    recorded.parent.mkdir(parents=True)
    recorded.write_bytes(b"another build's registry")

    _refused(_run(launcher, checkout, engine, distribution), engine, checkout,
             "already recorded here, from another build")
    assert recorded.read_bytes() == b"another build's registry"


@pytest.mark.parametrize("launcher", LAUNCHERS)
def test_a_release_holding_only_a_local_cook_s_registry_is_refused(launcher, tmp_path, checkout,
                                                                   engine):
    commit = _git(checkout, "rev-parse", "HEAD")
    distribution = _distribution(tmp_path, launcher, commit)
    metadata = _releases(checkout) / _git(checkout, "rev-parse", "--short", commit) / \
        launcher.platform / "Metadata"
    metadata.mkdir(parents=True)
    (metadata / "DevelopmentAssetRegistry.bin").write_bytes(b"a local cook's registry")

    _refused(_run(launcher, checkout, engine, distribution), engine, checkout,
             "holds a registry from a cook on this machine")
    assert not (metadata.parent / "AssetRegistry.bin").exists()


@pytest.mark.parametrize("launcher", LAUNCHERS)
def test_without_a_checkout_it_asks_for_one(launcher, tmp_path, engine):
    not_a_checkout = tmp_path / "world-tools-parent"
    not_a_checkout.mkdir()
    _refused(_run(launcher, not_a_checkout, engine, None), engine, not_a_checkout,
             "no CARLA checkout" if launcher.name == "sh" else "No CARLA checkout")
