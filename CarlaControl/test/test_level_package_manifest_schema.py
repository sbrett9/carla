"""A level package's `world.json` is what its schema says, and the installer applies the interface rule.

`PackageWorld.ps1` and `PackageWorld.sh` are run against a throwaway checkout holding one exported world
whose cook is already staged, so `-SkipCook` / `--skip-cook` packages it without an engine. The
`world.json` each writes into its zip is checked against `level_package_manifest.schema.json`, which
is held equal to `LevelPackageSchema`.

Both scripts write the manifest as UTF-8 without a byte-order mark -- `PackageWorld.ps1` under Windows
PowerShell 5.1 too, where one is checked as well -- with `packagedAtUtc` to the millisecond, and every
string `PackageWorld.sh` writes escaped as JSON requires.

`InstallWorld.ps1` and `InstallWorld.sh` are then given hand-made world zips and a throwaway package
declaring a world interface version, to hold them to the rule the world interface page states: a world
installs where the package's Major equals the world's and the package's Minor is at least the world's.
Each reads that version from the `[WorldInterface]` section of the package's ini alone, as each
`PackageWorld` does, and each refuses a `world.json` of a newer `formatVersion`, `-Force` or not, and
reads one without it as format 1.

Skipped where PowerShell 7 (pwsh), or a bash that can read this machine's paths, is not installed.
`PackageWorld.sh` zips with `zip`, which Git for Windows does not ship, so the test puts a stand-in on
its path that zips with Python.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.LevelPackageSchema import LEVEL_MANIFEST_SCHEMA, LevelPackageSchema  # noqa: E402
from carlacontrol.SchemaPublication import SchemaPublication  # noqa: E402

WORLD = "Fixture_World"
SCRIPTS = _REPO / "Scripts"
PUBLISHED = _REPO / "CarlaControl" / "schemas" / LEVEL_MANIFEST_SCHEMA


def _bash() -> str | None:
    """A bash that understands this machine's paths: Git for Windows' on Windows, not WSL's."""
    if os.name != "nt":
        return shutil.which("bash")
    git = shutil.which("git")
    if git is None:
        return None
    for folder in Path(git).resolve().parents[:3]:
        candidate = folder / "bin" / "bash.exe"
        if candidate.is_file():
            return str(candidate)
    return None


PWSH = shutil.which("pwsh")
# Windows PowerShell 5.1, whose Set-Content -Encoding UTF8 writes a byte-order mark.
WINDOWS_POWERSHELL = shutil.which("powershell") if os.name == "nt" else None
BASH = _bash()
PS1 = pytest.param("ps1", id="ps1", marks=pytest.mark.skipif(PWSH is None, reason="needs pwsh"))
PS51 = pytest.param("ps51", id="ps51", marks=pytest.mark.skipif(
    WINDOWS_POWERSHELL is None, reason="needs Windows PowerShell 5.1"))
SH = pytest.param("sh", id="sh", marks=pytest.mark.skipif(BASH is None, reason="needs bash"))
UTC_MILLISECONDS = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z")
BYTE_ORDER_MARK = b"\xef\xbb\xbf"


def posix(path: Path) -> str:
    return path.as_posix()


def git(checkout: Path, *arguments: str) -> str:
    environment = {**os.environ, "GIT_AUTHOR_NAME": "fixture", "GIT_AUTHOR_EMAIL": "f@example.com",
                   "GIT_COMMITTER_NAME": "fixture", "GIT_COMMITTER_EMAIL": "f@example.com"}
    return subprocess.run(["git", "-C", str(checkout), *arguments], capture_output=True, text=True,
                          check=True, env=environment).stdout.strip()


def test_the_published_schema_is_the_generated_one():
    assert PUBLISHED.read_text(encoding="utf-8") == SchemaPublication.text(LevelPackageSchema.schema()), (
        "regenerate with: python -c \"from carlacontrol.LevelPackageSchema import LevelPackageSchema; "
        "LevelPackageSchema.write('CarlaControl/schemas')\"")


# -- what PackageWorld writes -------------------------------------------------------------------------

def checkout_with_a_staged_world(root: Path, cook_platform: str) -> Path:
    """A CARLA checkout in miniature: one exported world marked for separate delivery, its cook
    staged as UAT stages a plugin, the world interface declaration and the release version, all
    committed, and the release folder the cook would read."""
    project = root / "Unreal" / "CarlaUnreal"
    plugin = project / "Plugins" / "GeneratedWorlds" / WORLD
    plugin.mkdir(parents=True)
    (project / "CarlaUnreal.uproject").write_text("{}\n", encoding="utf-8")
    (plugin / f"{WORLD}.uplugin").write_text("{}\n", encoding="utf-8")
    (plugin / "DeliverSeparately.txt").write_text("", encoding="utf-8")
    (project / "Config").mkdir()
    (project / "Config" / "DefaultWorldInterface.ini").write_text(
        "[WorldInterface]\nMajor=1\nMinor=3\n", encoding="utf-8")
    (project / "Content" / "Carla").mkdir(parents=True)
    (project / "Content" / "Carla" / "README").write_text("content\n", encoding="utf-8")
    (root / "CMakeLists.txt").write_text(
        "set (CARLA_VERSION_MAJOR 0)\nset (CARLA_VERSION_MINOR 10)\nset (CARLA_VERSION_PATCH 0)\n",
        encoding="utf-8")
    git(root, "init", "-q")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "fixture")
    (project / "Releases" / git(root, "log", "-1", "--format=%h") / cook_platform).mkdir(parents=True)
    # The staged cook is the build's output and is never committed. UAT nests it deeper; the
    # launchers find the folder named for the world that holds its descriptor anywhere under the
    # stage root.
    cooked = plugin / "Saved" / "StagedBuilds" / cook_platform / WORLD
    (cooked / "Maps").mkdir(parents=True)
    (cooked / f"{WORLD}.uplugin").write_text("{}\n", encoding="utf-8")
    (cooked / "Maps" / f"{WORLD}.umap").write_bytes(b"level")
    (cooked / "Maps" / f"{WORLD}.uasset").write_bytes(b"asset")
    return root


def zip_stand_in(folder: Path) -> Path:
    """A `zip` that runs `zip -qr <archive> .` with Python's zipfile."""
    folder.mkdir()
    script = folder / "zip"
    script.write_text(
        "#!/usr/bin/env bash\n"
        f'exec "{posix(Path(sys.executable))}" -c "import os, sys, zipfile\n'
        "with zipfile.ZipFile(sys.argv[1], 'w') as archive:\n"
        "    for top, _, files in os.walk('.'):\n"
        "        for name in files:\n"
        "            archive.write(os.path.join(top, name))\n"
        '" "${@: -2:1}"\n', encoding="utf-8", newline="\n")
    script.chmod(0o755)
    return folder


def powershell(launcher: str) -> str:
    """The PowerShell a launcher runs a .ps1 in: pwsh, or Windows PowerShell 5.1."""
    return WINDOWS_POWERSHELL if launcher == "ps51" else PWSH


def package_world(launcher: str, tmp_path: Path, interface_ini: str | None = None) -> dict:
    """Run one launcher's PackageWorld over the staged world and return the world.json it zipped."""
    platform = "Linux" if launcher == "sh" else "Windows"
    checkout = checkout_with_a_staged_world(tmp_path / "c", platform)
    if interface_ini is not None:
        (checkout / "Unreal" / "CarlaUnreal" / "Config" / "DefaultWorldInterface.ini").write_text(
            interface_ini, encoding="utf-8")
    engine = tmp_path / "UE"
    engine.mkdir()
    output = tmp_path / "out"
    if launcher != "sh":
        command = [powershell(launcher), "-NoProfile", "-NonInteractive", "-File",
                   str(SCRIPTS / "Windows" / "PackageWorld.ps1"), "-World", WORLD, "-CarlaRoot",
                   str(checkout), "-UnrealEngineRoot", str(engine), "-OutputDirectory", str(output),
                   "-SkipCook"]
        environment = dict(os.environ)
    else:
        command = [BASH, posix(SCRIPTS / "Linux" / "PackageWorld.sh"), "--world", WORLD,
                   "--carla-root", posix(checkout), "--unreal-engine-root", posix(engine),
                   "--output-directory", posix(output), "--skip-cook"]
        stand_in = zip_stand_in(tmp_path / "bin")
        environment = {**os.environ, "PATH": f"{stand_in}{os.pathsep}{os.environ['PATH']}"}
    completed = subprocess.run(command, capture_output=True, text=True, timeout=300,
                               encoding="utf-8", errors="replace", check=False, env=environment)
    assert completed.returncode == 0, completed.stdout + completed.stderr
    with zipfile.ZipFile(output / f"{WORLD}.zip") as archive:
        names = archive.namelist()
        written = archive.read("world.json")
    assert not written.startswith(BYTE_ORDER_MARK), "world.json is written without a byte-order mark"
    manifest = json.loads(written.decode("utf-8"))
    assert any(name.replace("\\", "/").startswith(f"{WORLD}/") for name in names), names
    manifest["_checkout_commit"] = git(checkout, "rev-parse", "HEAD")
    manifest["_release"] = git(checkout, "log", "-1", "--format=%h")
    return manifest


@pytest.mark.parametrize("launcher", [PS1, PS51, SH])
def test_package_world_writes_a_manifest_its_schema_accepts(launcher, tmp_path):
    manifest = package_world(launcher, tmp_path)
    commit, release = manifest.pop("_checkout_commit"), manifest.pop("_release")
    assert SchemaPublication.problems(manifest, LevelPackageSchema.schema()) == []
    # Both scripts stamp the manifest to the millisecond.
    assert UTC_MILLISECONDS.fullmatch(manifest["packagedAtUtc"]), manifest["packagedAtUtc"]
    assert manifest["world"] == WORLD
    assert manifest["mapPackage"] == f"/{WORLD}/Maps/{WORLD}"
    assert (manifest["worldInterfaceMajor"], manifest["worldInterfaceMinor"]) == (1, 3)
    assert manifest["basedOnRelease"] == release
    assert manifest["releaseVersion"] == "0.10.0"
    assert manifest["config"] == "Development"
    assert manifest["platform"] == ("Linux" if launcher == "sh" else "Win64")
    assert manifest["carlaGitHash"] == commit
    assert manifest["contentGitHash"] == commit
    assert manifest["unrealGitHash"] == ""


# A key of another section, ahead of the world interface's, is never read as its version.
INI_WITH_ANOTHER_SECTION_FIRST = ("; What a delivered world can rely on.\n[Other]\nMajor=9\nMinor=9\n\n"
                                  "[worldinterface]\nmajor=1\nMINOR=3\n")


@pytest.mark.parametrize("launcher", [PS1, SH])
def test_package_world_reads_the_version_from_the_world_interface_section_alone(launcher, tmp_path):
    manifest = package_world(launcher, tmp_path, interface_ini=INI_WITH_ANOTHER_SECTION_FIRST)
    assert (manifest["worldInterfaceMajor"], manifest["worldInterfaceMinor"]) == (1, 3)


def json_string_of_package_world_sh() -> str:
    """`PackageWorld.sh`'s `json_string` function, as the script defines it."""
    text = (SCRIPTS / "Linux" / "PackageWorld.sh").read_text(encoding="utf-8")
    found = re.search(r"^json_string\(\) \{\n.*?^\}\n", text, re.MULTILINE | re.DOTALL)
    assert found, "PackageWorld.sh defines json_string"
    return found.group(0)


@pytest.mark.skipif(BASH is None, reason="needs bash")
def test_package_world_sh_writes_every_string_escaped_as_json_requires(tmp_path):
    awkward = ['plain', 'a "quoted" word', 'C:\\back\\slash', 'tab\there', 'line\nbreak',
               '\x01 and \x1f', 'caf\u00e9 \u6771\u4eac']
    script = tmp_path / "escape.sh"
    script.write_text(json_string_of_package_world_sh()
                      + 'while IFS= read -r -d "" value; do json_string "$value"; printf "\\n"; done\n',
                      encoding="utf-8", newline="\n")
    completed = subprocess.run([BASH, posix(script)], input="\0".join(awkward).encode("utf-8") + b"\0",
                               capture_output=True, timeout=60, check=False)
    assert completed.returncode == 0, completed.stderr
    lines = completed.stdout.decode("utf-8").split("\n")[:-1]
    assert [json.loads(line) for line in lines] == awkward


def test_a_manifest_missing_a_field_or_naming_another_platform_is_refused():
    manifest = {"formatVersion": 1, "world": WORLD, "mapPackage": f"/{WORLD}/Maps/{WORLD}",
                "worldInterfaceMajor": 1, "worldInterfaceMinor": 0, "basedOnRelease": "abc1234",
                "releaseVersion": "0.10.0", "config": "Development", "platform": "Mac",
                "carlaGitHash": "", "contentGitHash": "", "unrealGitHash": "",
                "packagedAtUtc": "2026-10-07T12:00:00Z"}
    problems = SchemaPublication.problems(manifest, LevelPackageSchema.schema())
    assert problems == ["$.platform: \"Mac\" is not one of ['Win64', 'Linux']"]
    del manifest["basedOnRelease"]
    manifest["platform"] = "Win64"
    assert SchemaPublication.problems(manifest, LevelPackageSchema.schema()) == [
        "$: 'basedOnRelease' is required"]


# -- InstallWorld applies the world interface rule ----------------------------------------------------

def world_zip(path: Path, major: int, minor: int, format_version: int | None = 1) -> Path:
    manifest = {"formatVersion": format_version, "world": WORLD, "mapPackage": f"/{WORLD}/Maps/{WORLD}",
                "worldInterfaceMajor": major, "worldInterfaceMinor": minor,
                "basedOnRelease": "abc1234", "releaseVersion": "0.10.0", "config": "Development",
                "platform": "Win64", "carlaGitHash": "", "contentGitHash": "", "unrealGitHash": "",
                "packagedAtUtc": "2026-10-07T12:00:00.0000000Z"}
    if format_version is None:
        del manifest["formatVersion"]
    elif format_version == 1:
        assert SchemaPublication.problems(manifest, LevelPackageSchema.schema()) == []
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("world.json", json.dumps(manifest, indent=2))
        archive.writestr(f"{WORLD}/{WORLD}.uplugin", "{}\n")
    return path


def package_declaring(root: Path, major: int, minor: int, ini: str | None = None) -> Path:
    config = root / "CarlaUnreal" / "Config"
    config.mkdir(parents=True)
    (config / "DefaultWorldInterface.ini").write_text(
        ini if ini is not None else f"[WorldInterface]\nMajor={major}\nMinor={minor}\n",
        encoding="utf-8")
    (root / "VERSION").write_text(f"Carla version:          0.10.0\nWorld interface:        "
                                  f"{major}.{minor}\n", encoding="utf-8")
    return root


def install(launcher: str, package: Path, into: Path, force: bool = False) -> subprocess.CompletedProcess:
    if launcher == "ps1":
        command = [PWSH, "-NoProfile", "-NonInteractive", "-File",
                   str(SCRIPTS / "Windows" / "InstallWorld.ps1"), "-Package", str(package), "-Into",
                   str(into), *(["-Force"] if force else [])]
    else:
        command = [BASH, posix(SCRIPTS / "Linux" / "InstallWorld.sh"), "--package", posix(package),
                   "--into", posix(into), *(["--force"] if force else [])]
    return subprocess.run(command, capture_output=True, text=True, timeout=300, encoding="utf-8",
                          errors="replace", check=False)


@pytest.mark.parametrize("launcher", [PS1, SH])
@pytest.mark.parametrize(("package_version", "world_version", "installs"), [
    ((1, 0), (1, 0), True),
    ((1, 7), (1, 2), True),
    ((1, 1), (1, 2), False),
    ((2, 0), (1, 0), False),
    ((1, 0), (2, 0), False),
])
def test_a_world_installs_only_where_major_matches_and_the_package_minor_is_at_least_the_world_s(
        launcher, package_version, world_version, installs, tmp_path):
    package = world_zip(tmp_path / f"{WORLD}.zip", *world_version)
    into = package_declaring(tmp_path / "package", *package_version)
    completed = install(launcher, package, into)
    installed = into / "CarlaUnreal" / "Plugins" / "GeneratedWorlds" / WORLD / f"{WORLD}.uplugin"
    assert (completed.returncode == 0) == installs, completed.stdout + completed.stderr
    assert installed.is_file() == installs
    if not installs:
        assert "This world was not built for this package" in completed.stdout + completed.stderr


def installed(into: Path) -> bool:
    return (into / "CarlaUnreal" / "Plugins" / "GeneratedWorlds" / WORLD / f"{WORLD}.uplugin").is_file()


@pytest.mark.parametrize("launcher", [PS1, SH])
@pytest.mark.parametrize("force", [False, True])
def test_a_world_json_of_a_newer_format_is_refused_whatever_force_says(launcher, force, tmp_path):
    package = world_zip(tmp_path / f"{WORLD}.zip", 1, 0, format_version=2)
    into = package_declaring(tmp_path / "package", 1, 0)
    completed = install(launcher, package, into, force=force)
    said = completed.stdout + completed.stderr
    assert completed.returncode == 1, said
    assert not installed(into)
    assert "declares formatVersion 2 in world.json, and this InstallWorld reads formatVersion 1 and " \
           "earlier" in " ".join(said.split())


@pytest.mark.parametrize("launcher", [PS1, SH])
def test_a_world_json_without_a_format_version_is_format_one(launcher, tmp_path):
    package = world_zip(tmp_path / f"{WORLD}.zip", 1, 0, format_version=None)
    into = package_declaring(tmp_path / "package", 1, 0)
    completed = install(launcher, package, into)
    assert completed.returncode == 0, completed.stdout + completed.stderr
    assert installed(into)


@pytest.mark.parametrize("launcher", [PS1, SH])
@pytest.mark.parametrize(("world_version", "installs"), [((1, 3), True), ((9, 0), False)])
def test_install_world_reads_the_package_s_version_from_the_world_interface_section_alone(
        launcher, world_version, installs, tmp_path):
    package = world_zip(tmp_path / f"{WORLD}.zip", *world_version)
    into = package_declaring(tmp_path / "package", 0, 0, ini=INI_WITH_ANOTHER_SECTION_FIRST)
    completed = install(launcher, package, into)
    assert (completed.returncode == 0) == installs, completed.stdout + completed.stderr
    assert installed(into) == installs

