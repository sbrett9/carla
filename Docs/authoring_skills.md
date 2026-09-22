# Authoring skills

Reference bundles that an AI coding assistant loads to work on this repository. Two kinds, kept
apart on purpose.

## Ours: `CarlaControl/skills/`

Skills that describe this fork's own tooling live in the repository, tracked and versioned with the
code they describe, and ship in the distribution under `skills/`.

| Skill | Covers |
|---|---|
| [`sumo-traffic-scenarios`](../CarlaControl/skills/sumo-traffic-scenarios/SKILL.md) | Building SUMO traffic scenarios and Cursor-on-Target telemetry datasets for a CARLA world generated from OpenStreetMap: the OSM to world-package to SUMO-network to routes to CoT pipeline, the netconvert flags, coordinate alignment, and the `make_*_scenario.py` / `sumo_cot_telemetry.py` tools |

They sit beside `CarlaControl/src/carlacontrol/`, the code that generates most of their contents, so
a skill cannot describe a tool the same commit changed. A copy outside the repository has no
version, no history and no reproducible source, and cannot be bundled into a distribution — which is
why the `.agents/skills/sumo-traffic-scenarios/` copy at the workspace root is now a stub pointing
here rather than a second, editable copy. It is deliberately not a directory junction: a junction is
invisible in `git status`, does not survive a fresh clone, and lets the two diverge unseen.

## Theirs: `CarlaControl/skills/third-party/`

General Unreal Engine 5 reference skills — gameplay framework, Mass Entity, Niagara, materials,
replication and about twenty more — are a third-party MIT-licensed collection, and they are
**vendored here** at `CarlaControl/skills/third-party/unreal-engine-skills/`, under the `third-party`
path segment that says whose they are. Assistants working in this workspace read them, so the version
they read needs a commit behind it rather than whatever a developer happened to clone.

| | |
|---|---|
| Upstream | <https://github.com/quodsoler/unreal-engine-skills> |
| Pinned commit | `231c8571be6f3335685edc566a28ec6f9621361d` |
| Licence | MIT, Copyright (c) 2025 quodsoler — the upstream text sits beside the skills as `LICENSE`, verbatim |
| Provenance and update procedure | [`PROVENANCE.md`](../CarlaControl/skills/third-party/unreal-engine-skills/PROVENANCE.md) beside them |

**They do not ship.** `MakeDistribution` copies `CarlaControl/skills/` into the distribution and skips
`third-party/` on both platforms. A distribution recipient authors scenarios against a generated
world; they do not write engine C++, so this is 1.3 MB of somebody else's content they have no use
for, and the `skills/` row in the distribution's generated `MANIFEST.md` states one provenance and one
licence for everything under it — which is true only while everything under it is ours.

**Do not edit the vendored files.** A local fix is invisible to upstream and is lost at the next
update. Replace the directory wholesale from a fresh clone and move the pin, here and in
`PROVENANCE.md`.

## What an assistant discovers

An assistant working in this workspace loads skills from `.agents/skills/` at the workspace root,
which is outside this repository and outside any repository:

```
<workspace>/
  carla/                        this repository
    CarlaControl/skills/
      sumo-traffic-scenarios/   ours, canonical
      third-party/
        unreal-engine-skills/   vendored, MIT, pinned
  unreal-engine-skills/         the upstream clone the vendored copy came from
  .agents/skills/               what an assistant discovers:
    sumo-traffic-scenarios/     a stub pointing at carla/CarlaControl/skills/
    ue-*/                       the Unreal Engine skills, byte-identical to the vendored copy
```

The `.agents/skills/ue-*` directories are what the harness actually reads, so they stay where they
are. The vendored copy is what gives them a version, a licence and a diff.
