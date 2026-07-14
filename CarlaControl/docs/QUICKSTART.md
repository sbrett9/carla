# CarlaControl Quick Start

## Installation

### Prerequisites

First, ensure CarlaNet is installed:

```bash
cd /home/cdavies/repo/carla/CarlaNet/python
./build_wheel.sh --editable
```

### Install in Development Mode

```bash
cd /home/cdavies/repo/carla/CarlaControl

# Using build script (recommended)
./build_wheel.sh --editable

# Or manually
pip install -e .
```

### Build Wheel

```bash
# Build only
./build_wheel.sh

# Build and install
./build_wheel.sh --install

# Clean build
./build_wheel.sh --clean
```

### Install with Development Tools

```bash
pip install -e ".[dev]"
```

## Verify Installation

```bash
python3 scripts/verify_setup.py
```

## Running Tests

```bash
pytest
```

## Code Quality

### Format Code

```bash
black src/ test/ scripts/
```

### Check Linting

```bash
ruff check src/ test/ scripts/
```

### Auto-fix Linting Issues

```bash
ruff check --fix src/ test/ scripts/
```

## Package Structure

```
CarlaControl/
├── src/carlacontrol/          # Main package source
│   ├── __init__.py            # Package initialization
│   └── version.py             # Version information
├── test/                      # Unit tests
│   └── test_version.py        # Example test
├── scripts/                   # Utility scripts
│   └── verify_setup.py        # Setup verification
├── docs/                      # Documentation
├── pyproject.toml             # Package configuration
├── python-rules.md            # Coding standards
├── README.md                  # Full documentation
├── QUICKSTART.md              # This file
├── MANIFEST.in                # Distribution manifest
└── .gitignore                 # Git ignore patterns
```

## Adding New Modules

1. Create module in `src/carlacontrol/`
2. Add proper file header (see `python-rules.md`)
3. Include module docstring
4. Add type hints to all functions
5. Create corresponding test in `test/`
6. Update `__init__.py` if exporting public API

## Example: Creating a New Module

```python
# src/carlacontrol/my_module.py
############################# INTELLECTUAL PROPERTY RIGHTS #############################
# ... (full header from python-rules.md)
#
#    File:    my_module.py
#    Author:  Your Name
#    Date:    2026-07-14
#
#    Purpose:  Brief description
#

"""Module docstring."""

import logging

import carlanet as carla

logger = logging.getLogger(__name__)


def my_function(param: str) -> int:
    """Brief description.
    
    Args:
        param: Description
        
    Returns:
        Description
    """
    return len(param)
```

## Testing

```python
# test/test_my_module.py
from carlacontrol.my_module import my_function


def test_my_function():
    """Test my_function."""
    result = my_function("test")
    assert result == 4
```

## Coding Standards

See `python-rules.md` for complete standards. Key points:

- Python 3.11+ required
- Use type hints everywhere
- Google-style docstrings
- Black for formatting (100 char line length)
- Ruff for linting
- Private members prefixed with `_`
- Use `X | None` instead of `Optional[X]`

## Dependencies

- **carlanet** (>=0.1.0): .NET client for CARLA
- **numpy** (>=1.24.0): Numerical computing

## Getting Help

- Review `README.md` for full documentation
- Check `python-rules.md` for coding standards
- Run `python3 scripts/verify_setup.py` to verify setup
