# CarlaControl

High-level control and automation tools for CARLA simulator.

## Overview

CarlaControl provides advanced control systems, automation utilities, and high-level interfaces for working with the CARLA simulator through CarlaNet. This package builds on top of CarlaNet to provide domain-specific functionality and simplified APIs for common simulation tasks.

## Installation

### Prerequisites

CarlaControl depends on CarlaNet. Ensure CarlaNet is built and available:

```bash
cd ../CarlaNet/python
./build_wheel.sh --editable
```

### Development Installation

From the CarlaControl directory:

```bash
# Quick editable install
./build_wheel.sh --editable

# Or manually with pip
pip install -e .
```

### Build Wheel

```bash
# Build wheel only
./build_wheel.sh

# Build and install
./build_wheel.sh --install

# Clean build
./build_wheel.sh --clean --install
```

### With Development Tools

```bash
pip install -e ".[dev]"
```

## Project Structure

```
CarlaControl/
├── src/
│   └── carlacontrol/       # Main package source
│       └── __init__.py
├── test/                   # Unit tests
├── docs/                   # Documentation
├── scripts/                # Utility scripts
├── pyproject.toml          # Package configuration
├── python-rules.md         # Python coding standards
└── README.md               # This file
```

## Development

### Code Style

This project follows the SNC Python Style Guide defined in `python-rules.md`. Key points:

- **Python Version:** 3.11+ (target: 3.12)
- **Formatting:** Black (line length: 100)
- **Linting:** Ruff
- **Type Hints:** Required for all public APIs
- **Docstrings:** Google-style

### Running Tests

```bash
pytest
```

### Code Formatting

```bash
# Format code
black src/ test/

# Check linting
ruff check src/ test/

# Auto-fix linting issues
ruff check --fix src/ test/
```

## Dependencies

### Required
- **numpy** (>=1.24.0): Numerical computing

### Optional
- **carlanet**: .NET client for CARLA simulator (installed from `../CarlaNet/python`)
- **pytest** (>=7.0): Testing framework (dev)
- **ruff** (>=0.1.0): Linting (dev)

## License

Copyright (c) 2026 Sierra Nevada Corporation. All Rights Reserved.

This software is proprietary and confidential. Unauthorized copying, distribution, or use is strictly prohibited.

## Contributing

This is an internal SNC project. All contributors must:

1. Follow the coding standards in `python-rules.md`
2. Include proper file headers with IP notice
3. Write tests for new functionality
4. Run Black and Ruff before committing
5. Use type hints for all public APIs

## Contact

**Maintainer:** SNC Team  
**Last Updated:** 2026-07-14
