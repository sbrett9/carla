# AI Agent Instructions for CARLA Project

**Project:** CARLA Simulator with CarlaControl Package  
**Organization:** Sierra Nevada Corporation (SNC)  
**Last Updated:** 2026-07-14

---

## Project Overview

This is a **proprietary SNC project** for CARLA autonomous driving simulation. The repository contains:

1. **CARLA Simulator** - Modified Unreal Engine fork (C++)
2. **CarlaNet** - .NET/C# client library with Python bindings at `CarlaNet/`
3. **CarlaControl** - High-level Python control package at `CarlaControl/`

Each component has its own coding standards and documentation.

---

## Critical Rules

### 1. Code Conventions (MUST FOLLOW - ALL LANGUAGES)

**Read `CLAUDE.md` immediately** - it defines mandatory conventions for all code:
- **No conversational jargon** in committed code (no "Option A", "Phase 2b", "as discussed")
- **Self-explanatory identifiers** - code must make sense without conversation context
- **Objective commit messages** - no transient labels or chat references

These rules apply to **C++, C#, Python, and all other code** in this repository.

### 2. Language-Specific Style Guides

**For Python work (CarlaControl):**
- Read `CarlaControl/docs/python-rules.md` for complete Python standards
- Python 3.11+, Black formatting, Ruff linting, type hints required

**For C++ work (CARLA core):**
- Follow Unreal Engine coding standards
- Check CARLA documentation for project-specific conventions

**For C#/.NET work (CarlaNet):**
- Follow .NET coding conventions
- Check `CarlaNet/` documentation for project-specific standards

---

## Project Architecture

### Directory Structure

```
carla/                              # Repository root (CARLA simulator fork)
├── CarlaNet/                       # .NET client library
│   └── python/                     # Python bindings
├── CarlaControl/                   # High-level Python package
│   ├── src/carlacontrol/           # Package source
│   ├── test/                       # Unit tests
│   ├── docs/                       # Documentation
│   │   └── python-rules.md         # Style guide
│   └── pyproject.toml              # Package config
├── CLAUDE.md                       # Code conventions (READ THIS)
├── AGENTS.md                       # This file (AI agent instructions)
└── Docs/                           # CARLA documentation
```

---

## Development Workflow

### Before Making Changes

1. **Read relevant documentation:**
   - `CLAUDE.md` - Code conventions (mandatory for all code)
   - **For Python:** `CarlaControl/docs/python-rules.md`
   - **For C++:** CARLA documentation and Unreal Engine standards
   - **For C#/.NET:** CarlaNet documentation

2. **Understand the component:**
   - **CARLA core:** C++ Unreal Engine project
   - **CarlaNet:** .NET library, requires .NET SDK
   - **CarlaControl:** Python package, requires CarlaNet installed

3. **Check existing code:**
   - Look for existing implementations before creating new code
   - Review related modules and tests
   - Follow existing patterns in that component

### Making Changes

1. **Preserve existing functionality** - don't break working code
2. **Write tests** for new code (TDD approach preferred)
3. **Follow language conventions:**
   - **Python:** Type hints, Google-style docstrings, Black/Ruff formatting
   - **C++:** Unreal Engine conventions, appropriate comments
   - **C#:** .NET conventions, XML documentation comments
4. **Run appropriate formatters/linters** before committing

### Testing

- **Unit tests:** High coverage for core functionality
- **Integration tests:** Cover component integration points
- **Run tests:** Use appropriate test framework for the language
  - Python: `pytest`
  - C++: Check CARLA test documentation
  - C#: `dotnet test`

### Commit Messages

Follow `CLAUDE.md` guidelines:
- Imperative subject ≤72 chars
- Describe **what changed and why**
- No conversational transients ("as discussed", "per chat", "finally")
- No live-plan labels ("Option A", "Phase 2b", "step N")


---

## Component Dependencies

### CARLA Simulator (C++)
- Unreal Engine 4/5
- See CARLA documentation for build requirements

### CarlaNet (C#/.NET)
- .NET SDK
- See `CarlaNet/` for specific requirements

### CarlaControl (Python)
- Python 3.11+
- CarlaNet (must be built first)
- See `CarlaControl/pyproject.toml` for full dependency list

---
## Known Issues & Solutions

### CarlaNet Issues
**Problem:** `ModuleNotFoundError: No module named 'carlanet'`  
**Solution:** Build and install CarlaNet first:
```bash
cd CarlaNet/python
./build_wheel.sh --editable
---

## Security & IP Considerations

- **All code is SNC proprietary** - include IP header in every new file
- **No external code without approval** - check licenses before adding dependencies
- **Cesium Ion tokens** - use environment variables, never hardcode
- **TAK integration** - consider network security for CoT telemetry
- **No secrets in commits** - use `.env` files (gitignored)

---

## Quality Standards

### Code Quality (All Languages)
- ✅ Follow `CLAUDE.md` conventions (no conversational jargon)
- ✅ Pass language-specific linters/formatters
- ✅ Include appropriate documentation
- ✅ Add SNC IP headers to all files

### Python-Specific
- ✅ Black formatting, Ruff linting
- ✅ Type hints on public APIs
- ✅ Google-style docstrings

### Testing
- ✅ High test coverage on core modules
- ✅ All tests passing

### Documentation
- ✅ Clear documentation for public APIs
- ✅ Usage examples where appropriate
- ✅ Inline documentation for complex logic

---

## AI Agent Behavior Guidelines

### DO:
- ✅ Read `CLAUDE.md` before making any changes (mandatory)
- ✅ Check component-specific documentation:
  - Python: `CarlaControl/docs/python-rules.md`
  - C++: CARLA documentation
  - C#: CarlaNet documentation
- ✅ Preserve existing functionality
- ✅ Write tests for new code
- ✅ Follow language-specific conventions
- ✅ Run appropriate formatters/linters
- ✅ Ask for clarification if requirements are unclear
- ✅ Reference specific files/lines when discussing code
- ✅ Propose minimal, focused changes

### DON'T:
- ❌ Use conversational jargon in code (see `CLAUDE.md`)
- ❌ Skip the SNC IP header in new files
- ❌ Commit code without running formatters/linters
- ❌ Add dependencies without checking component requirements
- ❌ Break existing functionality
- ❌ Delete or weaken existing tests
- ❌ Hardcode paths, tokens, or secrets

---

## Quick Reference Commands

### CARLA Simulator
```bash
# Start server
./CarlaUE4.sh -RenderOffScreen
```

### CarlaNet (C#/.NET)
```bash
cd CarlaNet/python
./build_wheel.sh --editable
```

### CarlaControl (Python)
```bash
# Setup
cd CarlaControl
python3.11 -m venv venv
source venv/bin/activate
pip install -e ".[dev]"

# Format & lint
black src/ test/
ruff check --fix src/ test/

# Run tests
pytest -v
```

---

## Contact & Support

**Maintainer:** SNC Team  
**Documentation:**
- CARLA: `Docs/`
- CarlaNet: `CarlaNet/`
- CarlaControl: `CarlaControl/docs/`

**Issues:** Track in project management system (not in code comments)

---

## Document Version

**Version:** 1.0  
**Created:** 2026-07-14  
**Last Updated:** 2026-07-14

---

**For component-specific documentation:**
- **CARLA:** See `Docs/`
- **CarlaNet:** See `CarlaNet/` directory
- **CarlaControl:** See `CarlaControl/docs/`
