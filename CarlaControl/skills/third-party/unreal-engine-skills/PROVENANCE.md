# Provenance — `unreal-engine-skills`

Somebody else's work, vendored here so that the version an assistant reads in this workspace has a
commit behind it. Nothing in this directory is ours.

| | |
|---|---|
| Upstream | <https://github.com/quodsoler/unreal-engine-skills> |
| Pinned commit | `231c8571be6f3335685edc566a28ec6f9621361d` (2026-03-01, *Initial release — 27 Unreal Engine C++ skills for AI agents*) |
| Licence | MIT, Copyright (c) 2025 quodsoler — the upstream text is beside this file as [`LICENSE`](LICENSE), copied verbatim |
| Contents | `README.md` and `skills/`, 27 skill directories, byte-for-byte as upstream holds them at that commit |
| Not copied | upstream's `.gitignore`, which ignores a macOS `.DS_Store` and would otherwise apply its rule inside this repository. Nothing else is omitted, and nothing is modified |

## What these are, and what they are not

General Unreal Engine 5 C++ reference material — the gameplay framework, Mass Entity, Niagara,
materials and rendering, replication, and about twenty more. They describe the engine, not this fork,
so nothing here is kept in step with `CarlaControl/` or with `Unreal/CarlaUnreal/`. The skills that
describe *this* repository's tooling are the sibling directories of `third-party/`, and those are ours
and are versioned with the code they document.

## They are developer aids and do not ship

`MakeDistribution` copies `CarlaControl/skills/` into the distribution's `skills/` slot and skips this
`third-party/` directory on both platforms (`Scripts/Windows/MakeDistribution.ps1`,
`Scripts/Linux/MakeDistribution.sh`). A distribution recipient builds scenarios against a generated
world; they do not write engine C++, so this is 1.3 MB of somebody else's content they have no use
for, and shipping it would put an MIT attribution obligation on a package that exists to carry the
SUMO toolchain. The exclusion is also what keeps the distribution's licence manifest honest: the
`skills/` row states one provenance and one licence, and that row is true only while everything under
it is ours.

## Updating

Replace the directory wholesale from a fresh clone at the new commit, keep `LICENSE` and this file
beside it, and update the pinned commit in the table above and in
[`Docs/authoring_skills.md`](../../../../Docs/authoring_skills.md). Do not edit the vendored files: a
local fix here is invisible to upstream and is lost at the next update.
