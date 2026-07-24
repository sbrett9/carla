# Python conventions — carla

Rules for authoring Python under `carla/`. These apply to
**new** code; retroactive migration of existing files is a separate effort.

Mechanical rules (line length, imports ordering, naming, unused args, etc.) are
enforced by `ruff.toml` in this directory. This document covers the conventions
ruff cannot check. Run `ruff check .` and `ruff format .` before committing.

Scope: applies to all `*.py` files in this tree.

---

## Type hints — union syntax

Use the modern union operator, not `typing.Optional` / `typing.Union`:

```python
def load(path: Path) -> Config | None: ...      # yes
def merge(a: int | str) -> list[int]: ...        # yes
```

```python
from typing import Optional, Union
def load(path: Path) -> Optional[Config]: ...    # no
def merge(a: Union[int, str]) -> List[int]: ...  # no
```

Also prefer built-in generics (`list`, `dict`, `tuple`, `set`) over the
`typing` aliases (`List`, `Dict`, ...). `ruff` (UP rules) will flag these.

Only reach for `Optional[X]` if a file must run on Python < 3.10, and say so in
a comment explaining the constraint. Default target is 3.12.

## Imports

- **All imports at the top of the file.** No imports inside functions or
  methods — the only exceptions are breaking a genuine circular import or
  guarding a truly optional dependency, and each such case gets a one-line
  comment saying which.
- Use **absolute imports** for anything outside the current package. Reserve
  relative imports (`from .foo import Bar`) for intra-package references inside
  `carlanet/` or `carlacontrol/`.
- **One symbol concern per import line group.** Let `ruff format` / isort order
  and split them; don't hand-order.

Ordering (isort enforces this; shown so you can read a diff at a glance):

```python
from __future__ import annotations   # only if needed

import os                            # standard library
from pathlib import Path

import numpy as np                   # third-party
import carla

from carlanet.geodesy import Coordinate   # first-party (the carlanet package)

from .terrain_source import TerrainSource  # local-folder (relative)
```

Module-level constants go directly after the imports, before any class.



## Logging

- Always prefer using the python logging module over print statements
- **Levels:** `debug` (diagnostic) → `info` (milestones) → `warning` (unexpected) → `error` (failure) → `critical` (fatal)


## Object-oriented design

The architecture is **class-based**. All functionality beyond the CLI `main`
entry point lives in a class. Standalone module-level functions are reserved for
small, stateless utilities (a one-off `clamp`, a formatter) — not core logic.

### One class per file

- One public class per file. Small private helper classes tightly bound to it
  may share the file; unrelated classes may not.
- **File name matches the class name in PascalCase**: `class TerrainManager`
  lives in `TerrainManager.py`.
- Tie functional concerns to the object that owns them, not merely to the file.

### Two class shapes

**1. Instantiable objects** — the default, for anything that holds state:

```python
class TerrainManager:
    """Owns the set of terrain sources and resolves elevations against them."""

    def __init__(self, dted_root: Path) -> None:
        self.dted_root = dted_root
        self.sources: list[TerrainSource] = []

    def add_source(self, source: TerrainSource) -> None:
        """Register a source, highest priority last."""
        self.sources.append(source)
```

**2. Static / class-method utilities** — stateless helpers grouped under a name:

```python
class CoordinateParser:
    """Parses coordinate strings into Coordinate value objects."""

    @staticmethod
    def parse_utm(zone: int, is_north: bool, easting: float, northing: float) -> Coordinate_UTM:
        """Build a UTM coordinate from raw components."""
        return Coordinate_UTM(zone, is_north, easting, northing)

    @classmethod
    def from_string(cls, coord_str: str) -> Coordinate:
        """Parse a coordinate from its string form."""
        ...
```

### Design guidelines

- **Encapsulation** — related data and the methods over it stay together.
- **Single responsibility** — one clear purpose per class.
- **Composition over inheritance** — compose objects; avoid deep hierarchies.
- **Explicit over implicit** — pass dependencies in; make relationships visible.

### Anti-patterns to avoid

- Multiple unrelated classes in one file.
- Standalone functions carrying core business logic.
- CLI/argument-parsing logic mixed into business-logic classes.
- God classes that do everything.

### CLI entry points

The `main` function and argument parsing are the sanctioned exception to the
"everything in a class" rule. Keep `main` thin: parse args, construct the
objects, call them. Business logic belongs in the classes it invokes, not in
`main`.


