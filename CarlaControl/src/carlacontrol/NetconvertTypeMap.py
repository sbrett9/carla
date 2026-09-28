"""A world's own netconvert edge types, layered over SUMO's, validated before the world is built.

netconvert decides a road's lanes, speed and permitted vehicle classes from its *type*. For an OSM
import the type is built from the way's tags -- `highway=service` is the type `highway.service` -- and
SUMO's own type map (`data/typemap/osmNetconvert.typ.xml`, compiled into netconvert as its default)
gives each type its values. A world whose roads need other values is built with a type map of its own:
a naval port whose airfield perimeter, apron and quays are all OSM service roads, which SUMO's map
opens to delivery vans, pedestrians and bicycles and to nothing else, so no naval or port-authority
vehicle can drive them. The map is declared beside the extract -- `<extract>.typ.xml`, found by name,
or named explicitly -- exactly as the world's areas of interest are.

**The permissions are the world's.** The type map reaches netconvert in the one invocation that writes
the world's network and its OpenDRIVE, so the network the world package carries -- the network every
scenario compiled against the world runs, byte for byte (`07_Scenario_Authoring.md` D7.9, D7.32) --
already admits what the map says. Nothing edits a network after netconvert has written it.

**SUMO's own map is named first.** `--type-files` replaces netconvert's built-in map rather than adding
to it (`NILoader::load`), so a map given alone would discard every road type it does not restate. The
world build passes the installation's `osmNetconvert.typ.xml` and then this file. netconvert reads
them in order and a later definition of an id replaces the earlier one attribute by attribute, keeping
every attribute it does not state (`NIXMLTypesHandler::myStartElement`, `NBTypeCont::insertEdgeType`):
a file that says only `<type id="highway.service" allow="..."/>` changes that type's permissions and
leaves its lanes, speed and priority SUMO's. *Measured* on the Shahid Bahonar extract: naming the
default map explicitly reproduces the world's network fingerprint exactly; adding a `highway.service`
line changes the permissions of service edges and no other edge attribute.

**What a type map cannot express.** A type is keyed on the tags netconvert builds type ids from
(`highway`, `railway`, `service`, `usage`, `bus`, `psv` and a few more), never on `access`. netconvert
1.27 reads `access` only as `access=no`, which it turns into "public transport, emergency and authority
only" (`NIImporter_OpenStreetMap.cpp:2118-2121`); `access=private` changes nothing. So a type map
cannot tell a private service road from a public one: it sets what every road of a type admits.

What is refused, before anything is built: a file that is not XML, a root element other than
`<types>`, and a `<type>` without an `id`. What it leaves to netconvert: an unknown vehicle class, which
netconvert refuses by name at the start of the conversion ("Unknown vehicle class ... encountered"), so
a second list of SUMO's classes is not kept here.
"""
from __future__ import annotations

import hashlib
import logging
import xml.etree.ElementTree as ET
from pathlib import Path

# The file an extract's types are declared in: `Import/Shahid_Bahonar_Port.osm` ->
# `Import/Shahid_Bahonar_Port.typ.xml`.
SOURCE_SUFFIX = ".typ.xml"

# SUMO's own OSM type map, relative to an installation's root.
DEFAULT_MAP = Path("data") / "typemap" / "osmNetconvert.typ.xml"

TYPE_FILES_OPTION = "--type-files"

logger = logging.getLogger(__name__)


class NetconvertTypeMapError(ValueError):
    """A type map that cannot be used, and every problem with it."""

    def __init__(self, source: str, problems: list[str]) -> None:
        self.source = source
        self.problems = problems
        super().__init__(f"{source}: " + "; ".join(problems))


class NetconvertTypeMap:
    """One world's type map: where it is, what it defines, and how netconvert is given it."""

    def __init__(self, path: Path, types: list[dict[str, str]], sha256: str) -> None:
        self.path = path
        self.types = types
        self.sha256 = sha256

    @staticmethod
    def beside(extract_path: str | Path) -> Path:
        """Where an extract's type map is declared: its own name, with `.typ.xml` for `.osm`."""
        extract = Path(extract_path)
        stem = extract.name[:-len(extract.suffix)] if extract.suffix else extract.name
        return extract.with_name(stem + SOURCE_SUFFIX)

    @classmethod
    def discover(cls, extract_path: str | Path) -> Path | None:
        """The extract's type map, when one exists beside it."""
        candidate = cls.beside(extract_path)
        return candidate if candidate.is_file() else None

    @classmethod
    def load(cls, path: str | Path) -> NetconvertTypeMap:
        """Read and validate a type map, refusing it whole with every problem found."""
        source = Path(path).resolve()
        raw = source.read_bytes()
        try:
            root = ET.fromstring(raw)
        except ET.ParseError as problem:
            raise NetconvertTypeMapError(source.name, [f"is not XML: {problem}"]) from None
        problems = []
        if root.tag != "types":
            problems.append(f"the root element is <{root.tag}>; a netconvert type map is <types>")
        types = []
        for index, element in enumerate(root.iter("type")):
            if not element.get("id", "").strip():
                problems.append(f"<type> number {index + 1} has no id, so it names no road type")
                continue
            types.append(dict(element.attrib))
        if problems:
            raise NetconvertTypeMapError(source.name, problems)
        return cls(source, types, hashlib.sha256(raw).hexdigest())

    @staticmethod
    def default_map(installation_home: str | Path) -> Path:
        """SUMO's own OSM type map in the installation whose netconvert builds the world."""
        return Path(installation_home).resolve() / DEFAULT_MAP

    def netconvert_arguments(self, installation_home: str | Path) -> list[str]:
        """`--type-files <SUMO's map>,<this map>`, absolute, in the order netconvert must read them.

        Raises FileNotFoundError when the installation carries no default map: this file alone would
        discard every road type it does not restate.
        """
        default = self.default_map(installation_home)
        if not default.is_file():
            raise FileNotFoundError(
                f"SUMO's own OSM type map is not at {default}; without it the type map {self.path} "
                "would replace every road type netconvert knows rather than add to them")
        return [TYPE_FILES_OPTION, f"{default},{self.path}"]

    def describe(self) -> list[str]:
        """One line per type the map defines, with the attributes it sets."""
        return [f"{entry['id']}: " + ", ".join(f"{key}={value}" for key, value in entry.items()
                                               if key != "id")
                for entry in self.types]
