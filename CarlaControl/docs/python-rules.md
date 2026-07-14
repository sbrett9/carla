# SNC Python Style Guide and Formatting Rules

This document defines the Python coding standards and formatting conventions for SNC projects.

---

## Table of Contents

1. [File Header](#file-header)
2. [Import Organization](#import-organization)
3. [Naming Conventions](#naming-conventions)
4. [Code Organization](#code-organization)
5. [Type Hints](#type-hints)
6. [Docstrings](#docstrings)
7. [Code Formatting](#code-formatting)
8. [Error Handling](#error-handling)
9. [Logging](#logging)
10. [Constants and Configuration](#constants-and-configuration)
11. [Performance](#performance)

---

## File Header

### Executable Scripts

**Scripts in the `scripts/` directory must be executable and include a shebang line:**

```python
#!/usr/bin/env python3
############################# INTELLECTUAL PROPERTY RIGHTS #############################
# ... (IP header continues)
```

After creating a script, make it executable:
```bash
chmod +x scripts/script_name.py
```

### Standard Header

Every Python file must begin with the standard intellectual property header followed by file metadata:

```python
############################# INTELLECTUAL PROPERTY RIGHTS #############################
##                                                                                    ##
##                   Copyright (c) 2026 Sierra Nevada Corporation                     ##
##                                All Rights Reserved.                                ##
##                                                                                    ##
##          Sierra Nevada Corporation (SNC) ("COMPANY") CONFIDENTIAL                  ##
##                                                                                    ##
##   Unpublished Copyright (c) 2026 Sierra Nevada Corporation, All Rights Reserved.   ##
##                                                                                    ##
##   NOTICE: All information contained herein is, and remains the property of         ##
##   Sierra Nevada Corporation.                                                       ##
##                                                                                    ##
##   The intellectual and technical concepts contained herein are proprietary to      ##
##   Sierra Nevada Corporation and may be covered by U.S. and Foreign Patents,        ##
##   patents in process, and are protected by trade secret or copyright law.          ##
##                                                                                    ##
##   Dissemination of this information or reproduction of this material is strictly   ##
##   forbidden unless prior written permission is obtained from Sierra Nevada         ##
##   Corporation.                                                                     ##
##                                                                                    ##
##   Access to the source code contained herein is hereby forbidden to anyone         ##
##   except current Sierra Nevada Corporation employees, managers or contractors      ##
##   who have executed Confidentiality and Non-disclosure agreements explicitly       ##
##   covering such access.                                                            ##
##                                                                                    ##
##   The copyright notice above does not evidence any actual or intended              ##
##   publication or disclosure of this source code, which includes information        ##
##   that is confidential and/or proprietary, and is a trade secret, of Sierra        ##
##   Nevada Corporation.                                                              ##
##                                                                                    ##
##   ANY REPRODUCTION, MODIFICATION, DISTRIBUTION, PUBLIC PERFORMANCE, OR PUBLIC      ##
##   DISPLAY OF OR THROUGH USE OF THIS SOURCE CODE WITHOUT THE EXPRESS WRITTEN        ##
##   CONSENT OF Sierra Nevada Corporation IS STRICTLY PROHIBITED, AND IN VIOLATION    ##
##   OF APPLICABLE LAWS AND INTERNATIONAL TREATIES. THE RECEIPT OR POSSESSION OF      ##
##   THIS SOURCE CODE AND/OR RELATED INFORMATION DOES NOT CONVEY OR IMPLY ANY         ##
##   RIGHTS TO REPRODUCE, DISCLOSE OR DISTRIBUTE ITS CONTENTS, OR TO MANUFACTURE,     ##
##   USE, OR SELL ANYTHING THAT IT MAY DESCRIBE, IN WHOLE OR IN PART.                 ##
##                                                                                    ##
############################# INTELLECTUAL PROPERTY RIGHTS #############################
#
#    File:    example_module.py
#    Author:  SNC Team
#    Date:    2026-03-27
#
#    Purpose:  Brief description of the module's purpose
#
```

After the header, include a module-level docstring:

```python
"""Brief one-line description of the module.

Optional longer description with more details about the module's
functionality, design decisions, or usage examples.
"""
```

---

## Import Organization

**All imports must be at the top of the file** immediately after the module docstring. Avoid local imports within functions or methods except when necessary for:
- Resolving circular import dependencies
- Optional dependencies that may not be installed
- Heavy imports that are rarely used (performance optimization)

### Import Structure

Group imports: stdlib → third-party → local, separated by blank lines. Sort alphabetically within each group.
```python
from __future__ import annotations  # Always first

import logging
import sys

import numpy as np
from PyQt6.QtCore import Qt, pyqtSignal

from carlanet.types import Actor
from carlanet.transport import Client
```

Initialize logger after imports:

```python
logger = logging.getLogger(__name__)
```

**Avoid local imports** except for circular dependencies, optional dependencies, or performance optimization.

---

## Naming Conventions

Follow PEP 8:

| Element | Convention | Example |
|---------|-----------|----------|
| Classes | PascalCase | `ActorBlueprint`, `Vector3D` |
| Functions/Methods | snake_case | `get_spawn_points()`, `_private_method()` |
| Variables | snake_case | `actor_id`, `_private_attr` |
| Constants | SCREAMING_SNAKE_CASE | `MAX_IMAGE_SIZE`, `DEFAULT_TIMEOUT` |
| Enum Values | SCREAMING_SNAKE_CASE | `TrafficLightState.RED` |

**Private members:** Prefix with single underscore `_`

**Acronyms:** Treat as words (`WalkerAiController`), except well-known ones (`Vector3D`, `Vector2D`)

```python
class Actor:
    def __init__(self, cs_actor, client):
        self._actor = cs_actor      # Private
        self.location = Location()  # Public
    
    def get_transform(self) -> Transform:  # Public method
        pass
    
    def _to_cs(self):  # Private method
        pass
```

---

## Code Organization

### One Class Per File

**Each Python file should contain exactly one class.** This promotes:
- Clear module boundaries and responsibilities
- Easier navigation and maintenance
- Better testability and reusability

```python
# Good: actor.py
class Actor:
    """Represents a CARLA actor."""
    pass

# Bad: actors.py with multiple classes
class Actor:
    pass

class Vehicle:
    pass

class Walker:
    pass
```

**Exception:** Small helper classes or enums that are tightly coupled to the main class may be included in the same file.

### Avoid Unencapsulated Functions

**Strongly prefer encapsulating functions within classes rather than having module-level functions.** If you have utility functions, wrap them in a static class:

```python
# Good: Use a static wrapper class
class GeometryUtils:
    """Utility functions for geometric calculations."""
    
    @staticmethod
    def distance_2d(p1: Vector2D, p2: Vector2D) -> float:
        """Calculate 2D distance between two points."""
        return ((p2.x - p1.x)**2 + (p2.y - p1.y)**2)**0.5
    
    @staticmethod
    def normalize_angle(angle: float) -> float:
        """Normalize angle to [-π, π]."""
        return (angle + 180) % 360 - 180

# Avoid: Dangling module-level functions
def distance_2d(p1: Vector2D, p2: Vector2D) -> float:
    """Calculate 2D distance between two points."""
    return ((p2.x - p1.x)**2 + (p2.y - p1.y)**2)**0.5

def normalize_angle(angle: float) -> float:
    """Normalize angle to [-π, π]."""
    return (angle + 180) % 360 - 180
```

**Benefits of static wrapper classes:**
- Clear namespace organization
- Easier to discover related functionality
- Better IDE support and autocomplete
- Simpler to mock in tests
- Consistent with object-oriented design principles

**Exception:** Module initialization code, and logger setup are acceptable at module level.

---

## Type Hints

Always use type hints with modern Python 3.10+ syntax:

```python
def process(
    items: list[str],
    config: dict[str, Any] | None = None
) -> tuple[int, str]:
    pass

class Actor:
    def __init__(self, cs_actor, client):
        self._actor = cs_actor
        self._client = client
        self._sub: Subscription | None = None
```

**Rules:**
- Use `X | None` instead of `Optional[X]`
- Use `list`, `dict`, `tuple` instead of `List`, `Dict`, `Tuple`
- Always type function signatures and return values

---

## Docstrings

Use Google-style docstrings. One-line for simple functions, full format for complex ones:

```python
def is_listening(self) -> bool:
    """Check if sensor is actively listening to stream."""
    return self._sub is not None

def listen(self, callback):
    """Subscribe to this sensor's data stream.
    
    The callback receives a high-level SensorData wrapper matched on
    this actor's type_id.
    
    Args:
        callback: Function called with sensor data on each frame
        
    Raises:
        RuntimeError: If actor has no sensor stream
    """
    pass
```

---

## Code Formatting

### Line Length

- **Maximum line length:** 100 characters
- Black formatter will handle line breaking automatically

### Formatting

- Use **4 spaces** per indentation level (never tabs)
- Run **Black** formatter before committing - it handles indentation, blank lines, and whitespace automatically
- Use **triple double quotes** for docstrings: `"""docstring"""`

### F-strings

Prefer f-strings for string formatting:

```python
# Good
logger.info(f"Loaded {len(gcps)} GCPs from {file_path}")
message = f"RMSE: {rmse:.2f} pixels"

# Acceptable for simple cases
message = "Hello " + name

# Avoid (old style)
message = "Loaded %d GCPs" % len(gcps)  # Don't use % formatting
message = "Loaded {} GCPs".format(len(gcps))  # Don't use .format()
```

---

## Error Handling

Catch specific exceptions and provide context:

```python
try:
    data = load_data(path)
except (FileNotFoundError, PermissionError) as e:
    logger.error(f"Failed to load data from {path}: {e}")
    return None
except Exception as e:
    logger.error(f"Unexpected error: {e}", exc_info=True)
    raise

# When raising exceptions
if not file_path.exists():
    raise FileNotFoundError(f"Image file not found: {file_path}")
```

**Never use bare `except:`** - always specify exception types.

---

## Logging

Initialize: `logger = logging.getLogger(__name__)`

**Levels:** `debug` (diagnostic) → `info` (milestones) → `warning` (unexpected) → `error` (failure) → `critical` (fatal)

```python
logger.info(f"Loaded {len(items)} items from {path}")
logger.error(f"Failed to process: {e}", exc_info=True)
```

**Best practices:** Include context, use f-strings, don't log secrets, use `exc_info=True` for tracebacks

---

## Constants and Configuration

```python
# Module constants (after imports and logger)
MAX_IMAGE_SIZE = 4096
DEFAULT_TIMEOUT = 30.0

# Configuration dataclasses
@dataclass
class AppConfig:
    """Application configuration."""
    name: str
    version: str
    servers: list[ServerConfig]

# Helper converters (prefix with _)
def _as_location(v):
    """Convert various types to Location."""
    if v is None:
        return Location()
    if isinstance(v, Location):
        return v
    return Location(float(v.x), float(v.y), float(v.z))
```

---

## Python Version

- **Minimum Python version:** 3.11
- **Target version:** 3.12
- Use modern Python features available in 3.11+ (e.g., `|` for unions, built-in generic types)

---

## Performance

Use `__slots__` for frequently-instantiated classes (geometry types, DTOs):

```python
class Vector3D:
    __slots__ = ("x", "y", "z")
    def __init__(self, x=0.0, y=0.0, z=0.0):
        self.x, self.y, self.z = float(x), float(y), float(z)
```

---

## Summary Checklist

When writing Python code for CarlaControl:
- [ ] Include standard file header with IP notice and metadata
- [ ] Scripts in `scripts/` directory: add shebang `#!/usr/bin/env python3` and make executable
- [ ] Organize imports into three sections (stdlib, third-party, CarlaControl)
- [ ] Use PascalCase for classes
- [ ] Use snake_case for functions, methods, and variables
- [ ] Use single underscore prefix `_` for private attributes/methods
- [ ] One class per file (with exceptions for tightly coupled helpers)
- [ ] Prefer static wrapper classes over unencapsulated module-level functions
- [ ] Use `X | None` instead of `Optional[X]` for type hints
- [ ] Write Google-style docstrings for public APIs
- [ ] Initialize logger with `logger = logging.getLogger(__name__)`
- [ ] Catch specific exceptions, not bare `except:`
- [ ] Use f-strings for string formatting
- [ ] Use `__slots__` for frequently-instantiated classes
- [ ] Run `black` and `ruff` before committing (handles formatting automatically)

---

**Document Version:** 1.0  
**Last Updated:** 2026-07-13  
**Maintainer:** SNC Team
