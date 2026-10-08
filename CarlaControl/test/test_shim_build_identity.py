"""The shim says what release it is, hands over what the server was built from, and names the program.

`carlanet.__version__` is the distribution's release number: what the wheel's build stamped, or, for the
shim run from a checkout, the checkout's own (`Util/ReleaseVersion.py`). `Client.get_build_identity` is
one call through `CarlaClient.GetBuildIdentityAsync`, handed over as a dict in the shape every record of
what made a file holds it; a server built before the call comes back not available, never raised. And
until a component declares the tool it runs as, the program's own name is the tool every CarlaNet writer
names.

The shim under test is this tree's, loaded by path under its own module name so the installed `carlanet`
is not what answers, over a stand-in client. It needs the CarlaNet assemblies (`CARLANET_PUBLISH_DIR`).
"""
from __future__ import annotations

import importlib.util
import os
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
_SHIM = _REPO / "CarlaNet" / "python" / "carlanet" / "__init__.py"

pytest.importorskip("clr_loader", reason="the shim loads the CarlaNet assemblies through pythonnet")
pytest.importorskip("pythonnet", reason="the shim loads the CarlaNet assemblies through pythonnet")


@pytest.fixture(scope="module")
def shim():
    """This tree's shim, loaded once for the process under the name the other shim tests use."""
    if not os.environ.get("CARLANET_PUBLISH_DIR") and not (_SHIM.parent / "dlls").is_dir():
        pytest.skip("the shim needs the CarlaNet assemblies: set CARLANET_PUBLISH_DIR to a publish")
    name = "carlanet_under_test"
    if name not in sys.modules:
        spec = importlib.util.spec_from_file_location(name, _SHIM)
        module = importlib.util.module_from_spec(spec)
        sys.modules[name] = module
        spec.loader.exec_module(module)
    try:
        from CarlaNet.Types.Provenance import ServerBuildIdentity  # noqa: F401
    except ImportError:
        pytest.skip("the loaded CarlaNet.Types predates the record; set CARLANET_PUBLISH_DIR to a fresh "
                    "publish")
    return sys.modules[name]


class _Answer:
    """A .NET Task as the shim waits on one."""

    def __init__(self, result) -> None:
        self._result = result

    def GetAwaiter(self):  # noqa: N802 -- the .NET member name
        return self

    def GetResult(self):  # noqa: N802 -- the .NET member name
        return self._result


class _Client:
    def __init__(self, identity) -> None:
        self.identity = identity
        self.asked = 0

    def GetBuildIdentityAsync(self):  # noqa: N802 -- the .NET member name
        self.asked += 1
        return _Answer(self.identity)


def _client(shim, identity):
    client = object.__new__(shim.Client)
    client._inner = _Client(identity)
    return client


def test_the_shim_from_a_checkout_is_the_checkout_s_release(shim):
    spec = importlib.util.spec_from_file_location("release_version_for_the_shim",
                                                  _REPO / "Util" / "ReleaseVersion.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if (_SHIM.parent / "_version.py").exists():
        pytest.skip("a stamp sits in the source tree; the build never writes one there")
    assert shim.__version__ == module.ReleaseVersion.of_checkout(_REPO)


def test_the_server_s_identity_is_handed_over_as_every_record_holds_it(shim):
    from CarlaNet.Types.Provenance import ServerBuildIdentity
    from System import String
    from System.Collections.Generic import Dictionary

    answer = Dictionary[String, String]()
    for key, value in {"release": "0.10.0", "world_interface": "1.0", "build": "editor",
                       "configuration": "Development", "carla_commit": "025443a83eaf1bb82f18795d608fca50eb77a452",
                       "content_commit": "unknown", "engine_commit": "unknown",
                       "commits_from": "compiled"}.items():
        answer[key] = value
    client = _client(shim, ServerBuildIdentity.FromAnswer(answer))

    identity = client.get_build_identity()

    assert identity == {"available": True, "release": "0.10.0", "world_interface": "1.0", "build": "editor",
                        "configuration": "Development",
                        "carla_commit": "025443a83eaf1bb82f18795d608fca50eb77a452",
                        "content_commit": "unknown", "engine_commit": "unknown", "commits_from": "compiled"}


def test_a_server_built_before_the_call_is_handed_over_as_not_saying(shim):
    from CarlaNet.Types.Provenance import ServerBuildIdentity

    client = _client(shim, ServerBuildIdentity.NotAnswered(
        "the server answers no get_build_identity: it was built before the call", "0.10.0", "1.0"))

    assert client.get_build_identity() == {
        "available": False, "release": "0.10.0", "world_interface": "1.0",
        "reason": "the server answers no get_build_identity: it was built before the call"}


def test_until_a_component_declares_itself_the_program_s_name_is_the_tool(shim):
    from CarlaNet.Types.Provenance import Producer

    Producer.ForgetDeclaredTool()
    tool = Producer.Tool
    assert str(tool.Item1) == shim._program_name()
    assert tool.Item2 is None
