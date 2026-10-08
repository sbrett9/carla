"""The distribution carries one release number, set once in CMakeLists.txt and stamped into both wheels.

`CARLA_VERSION` in the top-level `CMakeLists.txt` is the number (`Util/ReleaseVersion.py`). A build that
is not the tagged release names its commit as a PEP 440 local part, `0.10.0+g1a2b3c4d5`, with `.dirty`
where tracked files had changes, and `+unknown` where git cannot be asked. Asserted against throwaway
git checkouts, so nothing depends on the state of this one:

  * the number is read from CMakeLists.txt as text, with no CMake;
  * the local part follows the commit, the changes on top, the release tag, and git's absence;
  * both wheels built from one checkout report one version, the release plus the commit, stamped as
    `_version.py` in the built package and never in the source tree; a wheel built from a source
    distribution, which has no CMakeLists.txt, reads the stamp it carries; a tree with neither fails;
  * carlacontrol run from this checkout reports this checkout's version, and `RELEASE` its release.

The wheel builds need the `build` package and pip's access to setuptools, as the owner's wheel build
does; without them they are skipped.
"""
from __future__ import annotations

import importlib.util
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol import version as carlacontrol_version  # noqa: E402  (needs the path above)

_SPEC = importlib.util.spec_from_file_location("carla_release_version_under_test",
                                               _REPO / "Util" / "ReleaseVersion.py")
_MODULE = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(_MODULE)
ReleaseVersion = _MODULE.ReleaseVersion
ReleaseVersionError = _MODULE.ReleaseVersionError

CMAKE = """cmake_minimum_required (VERSION 3.27.2)
set (CARLA_VERSION_MAJOR {major})
set (CARLA_VERSION_MINOR {minor})
set (CARLA_VERSION_PATCH {patch})
"""
GIT = shutil.which("git")
needs_git = pytest.mark.skipif(GIT is None, reason="git is not on PATH")


def git(root: Path, *arguments: str) -> str:
    done = subprocess.run(["git", *arguments], cwd=root, capture_output=True, text=True, check=True)
    return done.stdout.strip()


def checkout(root: Path, release: tuple[int, int, int] = (0, 10, 0)) -> Path:
    """A git checkout holding a top-level CMakeLists.txt of `release`, committed."""
    root.mkdir(parents=True, exist_ok=True)
    major, minor, patch = release
    (root / "CMakeLists.txt").write_text(CMAKE.format(major=major, minor=minor, patch=patch),
                                         encoding="utf-8")
    git(root, "init", "-q")
    git(root, "config", "user.email", "release@example.invalid")
    git(root, "config", "user.name", "Release Test")
    git(root, "config", "commit.gpgsign", "false")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "release")
    return root


def test_the_release_is_what_the_top_level_cmakelists_sets_read_as_text():
    text = (_REPO / "CMakeLists.txt").read_text(encoding="utf-8")
    parts = [re.search(rf"set \(CARLA_VERSION_{name} (\d+)\)", text).group(1)
             for name in ("MAJOR", "MINOR", "PATCH")]
    assert ReleaseVersion.read_carla_version(_REPO / "CMakeLists.txt") == ".".join(parts)
    assert ReleaseVersion.is_checkout_root(_REPO)


def test_a_cmakelists_that_does_not_set_the_release_is_refused_by_name(tmp_path):
    (tmp_path / "CMakeLists.txt").write_text("project (other)\n", encoding="utf-8")
    with pytest.raises(ReleaseVersionError, match="CARLA_VERSION_MAJOR"):
        ReleaseVersion.read_carla_version(tmp_path / "CMakeLists.txt")
    assert not ReleaseVersion.is_checkout_root(tmp_path)


@needs_git
def test_a_build_names_its_commit_its_changes_and_only_the_clean_tagged_release_goes_without(tmp_path):
    root = checkout(tmp_path / "carla", (1, 4, 2))
    commit = git(root, "rev-parse", "--short=9", "HEAD")

    assert ReleaseVersion.of_checkout(root) == f"1.4.2+g{commit}"

    (root / "CMakeLists.txt").write_text(CMAKE.format(major=1, minor=4, patch=2) + "# edited\n",
                                         encoding="utf-8")
    assert ReleaseVersion.of_checkout(root) == f"1.4.2+g{commit}.dirty"
    # An untracked file is not a change to what was built.
    git(root, "checkout", "--", "CMakeLists.txt")
    (root / "notes.txt").write_text("scratch\n", encoding="utf-8")
    assert ReleaseVersion.of_checkout(root) == f"1.4.2+g{commit}"

    git(root, "tag", "v1.4.2")
    assert ReleaseVersion.of_checkout(root) == "1.4.2"
    # A tag of another release is not this one's.
    git(root, "tag", "-d", "v1.4.2")
    git(root, "tag", "1.4.1")
    assert ReleaseVersion.of_checkout(root) == f"1.4.2+g{commit}"


def test_a_tree_git_cannot_name_is_not_taken_for_the_release(tmp_path):
    (tmp_path / "CMakeLists.txt").write_text(CMAKE.format(major=0, minor=10, patch=0), encoding="utf-8")
    assert ReleaseVersion.of_checkout(tmp_path) == "0.10.0+unknown"


def test_carlacontrol_run_from_this_checkout_reports_this_checkout_s_version():
    if (_REPO / "CarlaControl" / "src" / "carlacontrol" / "_version.py").exists():
        pytest.skip("a stamp sits in the source tree; the build never writes one there")
    assert carlacontrol_version.__version__ == ReleaseVersion.of_checkout(_REPO)
    assert carlacontrol_version.RELEASE == ReleaseVersion.read_carla_version(_REPO / "CMakeLists.txt")


# -- the wheels ---------------------------------------------------------------------------------------

def _hermetic_checkout(root: Path) -> Path:
    """The two packages' build inputs, and the release they read, laid out as this checkout lays them
    out, committed in a git checkout of their own."""
    root.mkdir(parents=True)
    shutil.copy2(_REPO / "CMakeLists.txt", root / "CMakeLists.txt")
    (root / "Util").mkdir()
    shutil.copy2(_REPO / "Util" / "ReleaseVersion.py", root / "Util" / "ReleaseVersion.py")
    carlanet = root / "CarlaNet" / "python"
    (carlanet / "carlanet").mkdir(parents=True)
    for name in ("setup.py", "pyproject.toml"):
        shutil.copy2(_REPO / "CarlaNet" / "python" / name, carlanet / name)
    shutil.copy2(_REPO / "CarlaNet" / "python" / "carlanet" / "__init__.py", carlanet / "carlanet" / "__init__.py")
    control = root / "CarlaControl"
    control.mkdir()
    for name in ("setup.py", "pyproject.toml", "README.md", "LICENSE", "MANIFEST.in"):
        if (_REPO / "CarlaControl" / name).exists():
            shutil.copy2(_REPO / "CarlaControl" / name, control / name)
    shutil.copytree(_REPO / "CarlaControl" / "src" / "carlacontrol", control / "src" / "carlacontrol",
                    ignore=shutil.ignore_patterns("__pycache__", "_version.py"))
    # Whatever data folders the package maps, so its configuration reads as it does in the checkout.
    for data in ("catalogue", "schemas"):
        if (_REPO / "CarlaControl" / data).is_dir():
            shutil.copytree(_REPO / "CarlaControl" / data, control / data)
    git(root, "init", "-q")
    git(root, "config", "user.email", "release@example.invalid")
    git(root, "config", "user.name", "Release Test")
    git(root, "config", "commit.gpgsign", "false")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "release")
    return root


def _build(source: Path, out: Path, *flags: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, "-m", "build", *flags, "--outdir", str(out), str(source)],
                          capture_output=True, text=True, timeout=600, check=False)


def _wheel_version(wheel: Path, package: str) -> tuple[str, str]:
    """What a wheel's metadata says its version is, and what its stamped `_version.py` says."""
    with zipfile.ZipFile(wheel) as archive:
        metadata = next(name for name in archive.namelist() if name.endswith(".dist-info/METADATA"))
        declared = re.search(r"^Version: (.+)$", archive.read(metadata).decode("utf-8"), re.MULTILINE)
        stamp = archive.read(f"{package}/_version.py").decode("utf-8")
    stamped = re.search(r"^__version__ = '([^']+)'", stamp, re.MULTILINE)
    return declared.group(1).strip(), stamped.group(1)


@pytest.fixture(scope="module")
def built(tmp_path_factory):
    if importlib.util.find_spec("build") is None:
        pytest.skip("the 'build' package is not installed")
    if GIT is None:
        pytest.skip("git is not on PATH")
    root = _hermetic_checkout(tmp_path_factory.mktemp("release") / "carla")
    out = root.parent / "dist"
    wheels = {}
    for package, source, flags in (("carlanet", root / "CarlaNet" / "python", ("--wheel",)),
                                   # Through a source distribution: the wheel is built from the
                                   # unpacked sdist, which holds no CMakeLists.txt.
                                   ("carlacontrol", root / "CarlaControl", ())):
        done = _build(source, out, *flags)
        if done.returncode != 0:
            if "setuptools" in done.stdout + done.stderr and "satisf" in done.stdout + done.stderr:
                pytest.skip("pip could not fetch setuptools for the isolated build")
            pytest.fail(f"building {package} failed:\n{done.stdout}\n{done.stderr}")
        wheels[package] = next(out.glob(f"{package}-*.whl"))
    return root, wheels


def test_both_wheels_carry_the_release_cmakelists_sets_with_the_commit_that_built_them(built):
    root, wheels = built
    expected = f"{ReleaseVersion.read_carla_version(root / 'CMakeLists.txt')}+g" \
               f"{git(root, 'rev-parse', '--short=9', 'HEAD')}"

    for package, wheel in wheels.items():
        assert _wheel_version(wheel, package) == (expected, expected), package
        assert wheel.name.startswith(f"{package}-{expected}-"), wheel.name
    # Stamped into what was built, never into the sources.
    assert not (root / "CarlaNet" / "python" / "carlanet" / "_version.py").exists()
    assert not (root / "CarlaControl" / "src" / "carlacontrol" / "_version.py").exists()


def test_a_tree_with_no_cmakelists_and_no_stamp_will_not_build_a_wheel(built, tmp_path):
    root, _ = built
    bare = tmp_path / "elsewhere" / "python"
    shutil.copytree(root / "CarlaNet" / "python", bare,
                    ignore=shutil.ignore_patterns("build", "*.egg-info", "dist"))

    done = _build(bare, tmp_path / "dist", "--wheel")

    assert done.returncode != 0
    assert "cannot tell which release this carlanet is" in done.stdout + done.stderr
